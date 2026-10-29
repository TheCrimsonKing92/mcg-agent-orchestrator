using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using RemoteRun = RemoteLaneOccupancyHeartbeatTests.RemoteRun;

// Parallel-safe: fixture-owned storage and scripted processes; clocks and git are injected.
public sealed class ActivationRemoteLaneTailAdoptionScenarioTests
{
    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FreshRemoteTail_AdoptsOnlyAfterTrainTerminalAndReleased()
    {
        await using var remote = new RemoteRun();
        using var liveProbe = ReadHeartbeatFiles(remote);
        await using var scenario = new Scenario();
        await remote.StartAsync();
        await scenario.StartAsync();
        var start = remote.Clock.GetUtcNow();
        for (var tick = 1; tick <= 13; tick++)
        {
            if (tick > 1) await AdvanceAsync(remote, scenario);
            var sample = Sample(remote, tick);
            Assert.Equal(1, sample.Occupied);
            Assert.True(sample.LiveGates >= 1);
            await scenario.HeldTickAsync(tick, "admitted", sample: sample);
        }
        Assert.Equal(TimeSpan.FromMinutes(6), remote.Clock.GetUtcNow() - start);
        await scenario.HeldTickAsync(14, "terminal", sample: Sample(remote, 14));
        scenario.Tick(15, "released", sample: Sample(remote, 15));
        await scenario.AdoptedAsync();
        Assert.Equal(1, scenario.DiffCalls);
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
        await remote.AcceptAsync();
        Assert.Empty(remote.Running());
        await scenario.FinishAsync();
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task MissingOccupancyOrExpiredLease_RevertsAsAdmissionStalled(bool expireLease)
    {
        await using var remote = new RemoteRun(bindOccupancy: expireLease);
        using var liveProbe = ReadHeartbeatFiles(remote);
        await using var scenario = new Scenario();
        await remote.StartAsync();
        await scenario.StartAsync();
        var exited = false;
        var reverted = false;
        for (var tick = 1; tick <= 20; tick++)
        {
            if (tick > 1)
            {
                scenario.Clock.Advance(TimeSpan.FromSeconds(30));
                if (!exited)
                {
                    exited = expireLease && tick == 4; // t=90 s exceeds the 60 s lease.
                    await remote.AdvanceAsync(TimeSpan.FromSeconds(30), renew: !expireLease, expectExit: exited);
                }
                else remote.Clock.Advance(TimeSpan.FromSeconds(30));
            }
            var sample = Sample(remote, tick);
            Assert.Equal(1, sample.Occupied);
            if (expireLease && !exited) Assert.Equal(1, sample.LiveGates);
            else Assert.Equal(0, sample.LiveGates);
            if (exited)
            {
                Assert.Empty(remote.Running());
                Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, Assert.Single(remote.Rows()).Outcome);
            }
            if (!await scenario.ObserveAsync(sample)) { reverted = true; break; }
        }
        Assert.True(reverted, "The scripted admission samples never produced AdmissionStalled.");
        Assert.Contains(scenario.Lines, line => line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal)
            && line.Contains("reason=AdmissionStalled", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Lines, line => line.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(GateHeartbeatRunClass.Acceptance, 1)]
    [Xunit.InlineData(GateHeartbeatRunClass.FocusedEvidence, 0)]
    public void Heartbeat_DoesNotDuplicateTrain_AndFocusedEvidenceIsNotLive(string runClass, int live)
    {
        using var remote = new RemoteRun();
        using var liveProbe = ReadHeartbeatFiles(remote);
        var baseline = Census([]);
        var writer = new RemoteLaneOccupancyHeartbeat(RemoteRun.Goal, 0, remote.Storage, runClass, RemoteRun.RunId);
        writer.Beat("remote", remote.Clock.GetUtcNow(), remote.Clock.GetUtcNow());
        var occupants = GateLoadContextProbe.CaptureLiveGateOccupants(remote.Storage, remote.Clock.GetUtcNow());
        Assert.Single(occupants);
        Assert.Equal(live, occupants.Count(occupant => occupant.CountsAsAcceptanceOccupant));
        Assert.Equal(1, baseline.OccupiedCount);
        Assert.Equal(baseline.OccupiedCount, Census(occupants).OccupiedCount);
    }

    // The assembly initializer installs an empty probe; these scenarios need actual file evidence.
    private static IDisposable ReadHeartbeatFiles(RemoteRun remote) =>
        GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            GateLoadContextProbe.ReadLiveGateOccupants(remote.Storage, remote.Clock.GetUtcNow()));

    private static async Task AdvanceAsync(RemoteRun remote, Scenario scenario)
    {
        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        await remote.AdvanceAsync(TimeSpan.FromSeconds(30));
    }
    private static ActivationAdmissionSample Sample(RemoteRun remote, int tick)
    {
        var occupants = GateLoadContextProbe.CaptureLiveGateOccupants(remote.Storage, remote.Clock.GetUtcNow());
        return new(tick, false, 2, Census(occupants).OccupiedCount,
            occupants.Count(occupant => occupant.CountsAsAcceptanceOccupant));
    }
    private static ConductorBatchLoop.LiveAcceptanceCensus Census(IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> occupants) =>
        ConductorBatchLoop.BuildLiveAcceptanceCensus([], new HashSet<string>(),
            new([new(RemoteRun.RunId, new HashSet<string> { RemoteRun.Goal, "peer" })]), occupants);

    internal sealed class Scenario : IAsyncDisposable
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
                },
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));
            _run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", _shortTickGap ? "120" : "4000"],
                "C:\\fake-repo", _root, "default", "default", cancellationToken: _stop.Token);
            await Clock.WaitForCountAsync(1);
            Assert.NotNull(_successor);
        }

        internal void Probe(int tick, string phase, int waiting) => _successor!.OnStandardOutputLine!(
            new ActivationAdmissionProbeLine(tick, phase, phase == "none" ? "none" : "new-attempt", waiting).Format());

        internal void Drain() => _successor!.OnStandardOutputLine!("LOOP_RELAUNCH_DRAIN tick=1 active=1");

        internal void Tick(int tick, string phase = "none", int waiting = 2, bool emitProbe = true,
            ActivationAdmissionSample? sample = null)
        {
            // Production emits the probe before TICK_END. This fake releases after its tick instead:
            // release can adopt immediately, so complete the tick before asserting its healthy count.
            if (emitProbe && phase != "released") Probe(tick, phase, waiting);
            _successor!.OnStandardOutputLine!((sample ?? new ActivationAdmissionSample(tick, false, waiting, 0, 0)).Format());
            _successor.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            if (emitProbe && phase == "released") Probe(tick, phase, waiting);
        }

        internal async Task HeldTickAsync(int tick, string phase = "none", int waiting = 2, bool emitProbe = true,
            ActivationAdmissionSample? sample = null)
        {
            var count = Clock.Count + 1;
            Tick(tick, phase, waiting, emitProbe, sample);
            var registered = Clock.WaitForCountAsync(count);
            await Task.WhenAny(registered, _adopted.Task, _reverted.Task, _run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Events.Snapshot(), evt => evt.Operation == "activation" && evt.Status is "adopted" or "reverted");
            await registered;
        }

        internal async Task<bool> ObserveAsync(ActivationAdmissionSample sample)
        {
            var registered = Clock.WaitForCountAsync(Clock.Count + 1);
            Tick(sample.Tick, "admitted", sample: sample);
            await Task.WhenAny(registered, _reverted.Task, _run).WaitAsync(TestContext.Current.CancellationToken);
            if (_reverted.Task.IsCompleted || _run.IsCompleted)
            {
                await RestoredAsync("AdmissionStalled");
                return false;
            }
            await registered;
            return true;
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

    internal sealed class VirtualDeadlines : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new ManualRemoteLaneClock().GetUtcNow();
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
    internal sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
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
    internal sealed class RecordingEvents : IRunEventStore
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
