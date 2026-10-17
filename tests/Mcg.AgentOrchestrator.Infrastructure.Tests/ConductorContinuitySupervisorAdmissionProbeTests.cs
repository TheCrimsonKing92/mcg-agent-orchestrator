using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, injected git seam and clock, unique artifacts.
public sealed class ConductorContinuitySupervisorAdmissionProbeTests
{
    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task AdmissionLanding_ThreeTicks_WaitsForCompleteProbe()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        for (var tick = 1; tick <= 3; tick++) await scenario.HeldTickAsync(tick);
        await scenario.HeldTickAsync(4, "admitted");
        await scenario.HeldTickAsync(5, "terminal");
        scenario.Tick(6, "released");
        await scenario.AdoptedAsync();
        Assert.Contains("detail=healthyTicks=6", scenario.Activation("adopted").Detail);
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_PROBE_SKIPPED ", StringComparison.Ordinal));
        await scenario.FinishAsync();
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false, "ProbeNotAdmitted")]
    [Xunit.InlineData(true, "ProbeNotCompleted")]
    public async Task ProbeDeadline_PollWithoutNewOutput_RevertsAndRestores(bool admitted, string reason)
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, admitted ? "admitted" : "none");
        // The fake poll interval exceeds both probe deadlines, avoiding any tick-stall ambiguity.
        scenario.Clock.Advance(admitted ? AcceptanceAdmissionPathSet.CompletionTimeout : AcceptanceAdmissionPathSet.AdmissionTimeout);
        await scenario.RestoredAsync(reason);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task NoWaitingGoal_ThirdTick_RecordsSkipBeforeAdoption()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, waiting: 0);
        await scenario.HeldTickAsync(2, waiting: 0);
        scenario.Tick(3, waiting: 0);
        await scenario.AdoptedAsync();
        var lines = scenario.Lines.ToArray();
        var skipped = Array.FindIndex(lines, line => line.StartsWith("ACTIVATION_PROBE_SKIPPED reason=no-verified-goal-waiting ", StringComparison.Ordinal));
        var adopted = Array.FindIndex(lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
        Assert.True(skipped >= 0 && adopted > skipped);
        Assert.Equal("probe-skipped", scenario.Activation("probe-skipped").Status);
        await scenario.FinishAsync();
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("unrelated")]
    [Xunit.InlineData("absent")]
    [Xunit.InlineData("throws")]
    public async Task UnavailableOrUnrelatedDiff_AdoptsAtThirdTickWithoutProbeEvents(string seam)
    {
        await using var scenario = new Scenario(seam);
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, waiting: 5);
        await scenario.HeldTickAsync(2, waiting: 5);
        scenario.Tick(3, waiting: 5);
        await scenario.AdoptedAsync();
        Assert.Contains("detail=healthyTicks=3", scenario.Activation("adopted").Detail);
        Assert.Single(scenario.Events.Snapshot().Where(evt => evt.Operation == "activation"));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_PROBE_SKIPPED ", StringComparison.Ordinal));
        Assert.Equal(seam == "absent" ? 0 : 1, scenario.DiffCalls);
        await scenario.FinishAsync();
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task OldCandidateWithoutProbe_RecordsAbsentLineSkip()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, emitProbe: false);
        await scenario.HeldTickAsync(2, emitProbe: false);
        scenario.Tick(3, emitProbe: false);
        await scenario.AdoptedAsync();
        Assert.Contains("reason=probe-line-absent", scenario.Activation("probe-skipped").Detail);
        await scenario.FinishAsync();
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CompleteProbeBeforeThirdTick_StillNeedsThreeHealthyTicks()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1, "admitted");
        var next = scenario.Clock.Count + 1;
        scenario.Probe(1, "terminal", 2);
        scenario.Probe(1, "released", 2);
        await scenario.Clock.WaitForCountAsync(next);
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "adopted");
        await scenario.HeldTickAsync(2, "released");
        scenario.Tick(3, "released");
        await scenario.AdoptedAsync();
        await scenario.FinishAsync();
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ActiveProbe_DrainExtension_PreservesRemainingDuration()
    {
        await using var scenario = new Scenario(shortTickGap: true);
        await scenario.StartAsync();
        await scenario.HeldTickAsync(1);
        var gap = TimeSpan.FromSeconds(Math.Max(120, ConductorBatchLoop.WatchStopPollIntervalSeconds) + 10);
        await scenario.Clock.WaitForDurationAsync(gap);
        scenario.Clock.Advance(gap - TimeSpan.FromSeconds(1));
        scenario.Drain();
        var next = scenario.Clock.Count + 1;
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.Clock.WaitForCountAsync(next);
        Assert.Equal(gap - TimeSpan.FromSeconds(1), scenario.Clock.LastDuration);
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "reverted");
        await scenario.FinishAsync();
    }

    private sealed class Scenario : IAsyncDisposable
    {
        internal readonly RecordingEvents Events = new();
        internal readonly VirtualDeadlines Clock = new();
        internal readonly ConcurrentQueue<string> Lines = new();
        private readonly TaskCompletionSource _adopted = Signal();
        private readonly TaskCompletionSource _reverted = Signal();
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-supervisor-probe-{Guid.NewGuid():N}");
        private readonly MemoryStream _lease = new();
        private readonly string _seam;
        private readonly bool _shortTickGap;
        private readonly ScriptedHost _host;
        private ConductorSupervisorProcessRequest? _successor;
        private Task<int> _run = Task.FromResult(-1);
        internal int DiffCalls;
        private int _stops;

        internal Scenario(string seam = "touching", bool shortTickGap = false)
        {
            _seam = seam;
            _shortTickGap = shortTickGap;
            Events.OnAppend = evt =>
            {
                if (evt.Status == "adopted") _adopted.TrySetResult();
                if (evt.Status == "reverted") _reverted.TrySetResult();
            };
            _host = new ScriptedHost((request, index, token) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
                }
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                if (index == 1)
                {
                    _successor = request;
                    token.Register(() =>
                    {
                        if (_exit.TrySetResult(new(-1, 101, TerminationConfirmed: true))) Interlocked.Increment(ref _stops);
                    });
                    return _exit.Task;
                }
                Assert.Equal(2, index);
                // Restore has waiting work and deliberately no probe: it must finish at tick 3.
                for (var tick = 1; tick <= 3; tick++)
                {
                    request.OnStandardOutputLine(new ActivationAdmissionSample(tick, false, 5, 0, 0).Format());
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
            });
        }

        internal async Task StartAsync()
        {
            var stages = 0;
            var successor = new ConductorPreparedSuccessor("C:\\next", "C:\\next\\app.dll", "head-next", "head-next", "LOOP_START selfCheck=true", _lease);
            var supervisor = new ConductorContinuitySupervisor(_host, Events, timeProvider: Clock,
                inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
                stageSuccessor: _ => stages++ == 0 ? successor : null!,
                appendConductEvent: (_, _, line) => Lines.Enqueue(line),
                activationHealthyTicks: 3, activationDelay: Clock.Delay,
                activationStallTimeout: _shortTickGap ? TimeSpan.FromSeconds(10) : TimeSpan.FromHours(2),
                changedPathsBetween: _seam == "absent" ? null : (from, to) =>
                {
                    DiffCalls++;
                    Assert.Equal("incumbent", from);
                    Assert.Equal("head-next", to);
                    if (_seam == "throws") throw new IOException("fake git failed");
                    return _seam == "unrelated"
                        ? ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", "docs/operator-runbook.md"]
                        : ["src/Mcg.AgentOrchestrator.App/Orchestration/AcceptanceHolderLease.cs"];
                });
            _run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", _shortTickGap ? "120" : "4000"],
                "C:\\fake-repo", _root, "default", "default", cancellationToken: _stop.Token);
            await Clock.WaitForCountAsync(1);
            Assert.NotNull(_successor);
        }

        internal void Probe(int tick, string phase, int waiting) => _successor!.OnStandardOutputLine!(
            new ActivationAdmissionProbeLine(tick, phase, phase == "none" ? "none" : "new-attempt", waiting).Format());

        internal void Drain() => _successor!.OnStandardOutputLine!("LOOP_RELAUNCH_DRAIN tick=1 active=1");

        internal void Tick(int tick, string phase = "none", int waiting = 2, bool emitProbe = true)
        {
            if (emitProbe) Probe(tick, phase, waiting);
            _successor!.OnStandardOutputLine!(new ActivationAdmissionSample(tick, false, waiting, 0, 0).Format());
            _successor.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
        }

        internal async Task HeldTickAsync(int tick, string phase = "none", int waiting = 2, bool emitProbe = true)
        {
            var count = Clock.Count + 1;
            Tick(tick, phase, waiting, emitProbe);
            var registered = Clock.WaitForCountAsync(count);
            await Task.WhenAny(registered, _adopted.Task, _reverted.Task, _run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Events.Snapshot(), evt => evt.Operation == "activation" && evt.Status is "adopted" or "reverted");
            await registered;
        }

        internal RunEventAppend Activation(string status) => Assert.Single(Events.Snapshot(), evt => evt.Operation == "activation" && evt.Status == status);
        internal async Task AdoptedAsync()
        {
            await Task.WhenAny(_adopted.Task, _reverted.Task, _run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.StartsWith("ACTIVATION_ADOPTED ", Activation("adopted").Detail);
            Assert.False(_reverted.Task.IsCompleted);
        }
        internal async Task RestoredAsync(string reason)
        {
            Assert.Equal(0, await _run.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Contains($"reason={reason}", Activation("reverted").Detail);
            Assert.StartsWith("ACTIVATION_RESTORED ", Activation("restored").Detail);
            Assert.Single(Events.Snapshot().Where(evt => evt.Status == "reverted"));
            Assert.DoesNotContain(Events.Snapshot(), evt => evt.Status is "failed-both" or "probe-skipped");
            Assert.Equal(3, _host.Requests.Count);
            Assert.Null(_host.Requests[2].CommandPrefix);
            Assert.Equal(1, _stops);
            Assert.Equal(1, DiffCalls);
            Assert.Throws<ObjectDisposedException>(() => _lease.ReadByte());
        }
        internal async Task FinishAsync()
        {
            ConductorContinuityExitArtifact.Write(_successor!.ExitArtifactPath, new("stop-file", 6, 0, false));
            _exit.TrySetResult(new(0, 101));
            Assert.Equal(0, await _run.WaitAsync(TestContext.Current.CancellationToken));
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _run; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
            _lease.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class VirtualDeadlines : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _pending = [];
        private TaskCompletionSource _registered = Signal();
        private int _count;
        private TimeSpan _lastDuration;
        internal int Count { get { lock (_gate) return _count; } }
        internal TimeSpan LastDuration { get { lock (_gate) return _lastDuration; } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            lock (_gate)
            {
                var completion = Signal();
                token.Register(() => completion.TrySetCanceled(token));
                _pending.Add((_now + duration, completion));
                _lastDuration = duration;
                _count++;
                var prior = _registered;
                _registered = Signal();
                prior.TrySetResult();
                return completion.Task;
            }
        }
        internal void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
                foreach (var entry in _pending.Where(entry => entry.Due <= _now)) entry.Completion.TrySetResult();
                _pending.RemoveAll(entry => entry.Completion.Task.IsCompleted);
            }
        }
        internal async Task WaitForCountAsync(int count)
        {
            while (true)
            {
                Task registered;
                lock (_gate) { if (_count >= count) return; registered = _registered.Task; }
                await registered.WaitAsync(TestContext.Current.CancellationToken);
            }
        }
        internal async Task WaitForDurationAsync(TimeSpan duration)
        {
            while (true)
            {
                Task registered;
                lock (_gate) { if (_lastDuration == duration) return; registered = _registered.Task; }
                await registered.WaitAsync(TestContext.Current.CancellationToken);
            }
        }
    }
    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, token);
        }
    }
    private sealed class RecordingEvents : IRunEventStore
    {
        private readonly object _gate = new();
        private readonly List<RunEventAppend> _events = [];
        internal Action<RunEventAppend>? OnAppend;
        internal RunEventAppend[] Snapshot() { lock (_gate) return [.. _events]; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(evt);
                OnAppend?.Invoke(evt);
                return Task.FromResult(new RunEventRecord(_events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                    evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
            }
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
