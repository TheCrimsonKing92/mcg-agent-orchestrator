using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, a private clock, and a unique artifact directory per case.
public sealed class ConductorContinuitySupervisorAdmissionHealthTests
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(true, "AdmissionCensusFailing")]
    [Xunit.InlineData(false, "AdmissionStalled")]
    public async Task StreakAtLimit_StopsSuccessorAndRestoresPreviousBuild(bool failed, string reason)
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        var limit = failed ? ActivationAdmissionHealthPolicy.CensusFailureTicks :
            ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks;
        for (var tick = 1; tick < limit; tick++)
            await scenario.EmitHeldTickAsync(tick, failed, waiting: 3, occupied: 13);
        scenario.EmitTick(limit, failed, waiting: 3, occupied: 13);
        await scenario.AssertRestoredAsync(reason);
        var reverted = scenario.Activation("reverted");
        Assert.Contains(failed ? "censusFailedTicks=8 firstTick=1 lastTick=8" :
            "occupiedNoLiveGateTicks=12 waiting=3 occupied=13 live=0", reverted.Detail);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ThreeTicksWithOpenStreak_AdoptsOnlyAfterCompletedClearTick()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        for (var tick = 1; tick <= 3; tick++) await scenario.EmitHeldTickAsync(tick, failed: true);
        scenario.EmitTick(4, failed: false);
        await scenario.WaitForAdoptionAsync(4);
        await scenario.FinishAsync();
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "reverted");
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task SevenFailuresThenHealthyTick_AdoptsWithoutRevert()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        for (var tick = 1; tick < ActivationAdmissionHealthPolicy.CensusFailureTicks; tick++)
            await scenario.EmitHeldTickAsync(tick, failed: true);
        scenario.EmitTick(8, failed: false);
        await scenario.WaitForAdoptionAsync(8);
        await scenario.FinishAsync();
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "reverted");
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task BusyLiveGate_AdoptsAtThreeTicksAndRemainsAdopted()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        for (var tick = 1; tick <= 2; tick++)
            await scenario.EmitHeldTickAsync(tick, failed: false, waiting: 3, occupied: 1, live: 1);
        scenario.EmitTick(3, failed: false, waiting: 3, occupied: 1, live: 1);
        await scenario.WaitForAdoptionAsync(3);
        for (var tick = 4; tick <= 30; tick++)
            scenario.EmitTick(tick, failed: false, waiting: 3, occupied: 1, live: 1);
        await scenario.FinishAsync();
        Assert.Single(scenario.ConductLines.Where(line => line.StartsWith("ACTIVATION_", StringComparison.Ordinal)));
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FailuresAfterAdoption_DoNotRevertOrRecordAnotherActivation()
    {
        await using var scenario = new Scenario();
        await scenario.StartAsync();
        for (var tick = 1; tick <= 2; tick++) await scenario.EmitHeldTickAsync(tick, failed: false);
        scenario.EmitTick(3, failed: false);
        await scenario.WaitForAdoptionAsync(3);
        for (var tick = 4; tick <= 53; tick++) scenario.EmitTick(tick, failed: true);
        await scenario.FinishAsync();
        var activation = Assert.Single(scenario.ConductLines.Where(line => line.StartsWith("ACTIVATION_", StringComparison.Ordinal)));
        Assert.StartsWith("ACTIVATION_ADOPTED ", activation);
        Assert.Single(scenario.Events.Snapshot().Where(evt => evt.Operation == "activation"));
        Assert.Equal(0, scenario.SuccessorStops);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task FailedHealthInRestoreWindow_DoesNotTriggerSecondRevert()
    {
        await using var scenario = new Scenario(restoreFailedHealth: true);
        await scenario.StartAsync();
        for (var tick = 1; tick < ActivationAdmissionHealthPolicy.CensusFailureTicks; tick++)
            await scenario.EmitHeldTickAsync(tick, failed: true);
        scenario.EmitTick(8, failed: true);
        await scenario.AssertRestoredAsync("AdmissionCensusFailing");
        Assert.Single(scenario.Events.Snapshot().Where(evt => evt.Status == "reverted"));
        Assert.DoesNotContain(scenario.Events.Snapshot(), evt => evt.Status == "failed-both");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public readonly RecordingEvents Events = new();
        public readonly VirtualDeadlines Deadlines = new();
        public readonly ConcurrentQueue<string> ConductLines = new();
        private readonly TaskCompletionSource _adopted = NewSignal();
        private readonly TaskCompletionSource _reverted = NewSignal();
        private readonly MemoryStream _successorLease = new();
        private readonly CancellationTokenSource _stop =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _successorExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _artifacts = Path.Combine(Path.GetTempPath(), $"mcg-admission-activation-{Guid.NewGuid():N}");
        private ConductorSupervisorProcessRequest? _successor;
        private int _successorStops;

        public Scenario(bool restoreFailedHealth = false)
        {
            Events.OnAppend = evt =>
            {
                if (evt.Operation == "activation" && evt.Status == "adopted") _adopted.TrySetResult();
                if (evt.Operation == "activation" && evt.Status == "reverted") _reverted.TrySetResult();
            };
            Host = new ScriptedHost((request, index, token) =>
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
                        if (_successorExit.TrySetResult(new(-1, 101, TerminationConfirmed: true)))
                            Interlocked.Increment(ref _successorStops);
                    });
                    return _successorExit.Task;
                }
                Assert.Equal(2, index);
                var ticks = restoreFailedHealth ? ActivationAdmissionHealthPolicy.CensusFailureTicks : 3;
                for (var tick = 1; tick <= ticks; tick++)
                {
                    request.OnStandardOutputLine(new ActivationAdmissionSample(tick, restoreFailedHealth, 3, 13, 0).Format());
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", ticks, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
            });
        }

        public ScriptedHost Host { get; }
        public Task<int> Run { get; private set; } = Task.FromResult(-1);
        public int SuccessorStops => Volatile.Read(ref _successorStops);

        public async Task StartAsync()
        {
            var successor = new ConductorPreparedSuccessor(
                "C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll",
                "head-next", "head-next", "LOOP_START selfCheck=true", _successorLease);
            var stageCount = 0;
            var supervisor = new ConductorContinuitySupervisor(Host, Events,
                timeProvider: Deadlines, inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)],
                stageSuccessor: _ => stageCount++ == 0 ? successor : null!,
                appendConductEvent: (_, _, detail) => ConductLines.Enqueue(detail),
                activationHealthyTicks: 3, activationDelay: Deadlines.Delay,
                activationStallTimeout: TimeSpan.FromMinutes(2));
            Run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "120"],
                "C:\\repo", _artifacts, "default", "default", cancellationToken: _stop.Token);
            await Deadlines.WaitForCountAsync(1);
        }

        public void EmitTick(int tick, bool failed, int waiting = 0, int occupied = 0, int live = 0)
        {
            _successor!.OnStandardOutputLine!(new ActivationAdmissionSample(tick, failed, waiting, occupied, live).Format());
            _successor.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
        }

        public async Task EmitHeldTickAsync(int tick, bool failed, int waiting = 0, int occupied = 0, int live = 0)
        {
            var next = Deadlines.Count + 1;
            EmitTick(tick, failed, waiting, occupied, live);
            var observed = Deadlines.WaitForCountAsync(next);
            await Task.WhenAny(observed, _adopted.Task, _reverted.Task).WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Events.Snapshot(), evt => evt.Operation == "activation" &&
                evt.Status is "adopted" or "reverted");
            await observed;
        }

        public RunEventAppend Activation(string status) => Assert.Single(Events.Snapshot(),
            evt => evt.Operation == "activation" && evt.Status == status);

        public async Task WaitForAdoptionAsync(int tick)
        {
            await Task.WhenAny(_adopted.Task, _reverted.Task, Run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains($"detail=healthyTicks={tick}", Activation("adopted").Detail);
            Assert.False(_reverted.Task.IsCompleted);
        }

        public async Task AssertRestoredAsync(string reason)
        {
            Assert.Equal(0, await Run.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Contains($"reason={reason}", Activation("reverted").Detail);
            Assert.StartsWith("ACTIVATION_REVERTED ", Activation("reverted").Detail);
            Assert.StartsWith("ACTIVATION_RESTORED ", Activation("restored").Detail);
            Assert.Equal(1, SuccessorStops);
            Assert.Equal(3, Host.Requests.Count);
            Assert.Null(Host.Requests[2].CommandPrefix);
            Assert.Throws<ObjectDisposedException>(() => _successorLease.ReadByte());
        }

        public async Task FinishAsync()
        {
            ConductorContinuityExitArtifact.Write(_successor!.ExitArtifactPath, new("stop-file", 53, 0, false));
            _successorExit.TrySetResult(new(0, 101));
            Assert.Equal(0, await Run.WaitAsync(TestContext.Current.CancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await Run; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
            _successorLease.Dispose();
            if (Directory.Exists(_artifacts)) Directory.Delete(_artifacts, recursive: true);
        }
    }

    private sealed class VirtualDeadlines : TimeProvider
    {
        private readonly object _gate = new();
        private TaskCompletionSource _registered = NewSignal();
        private int _count;
        public int Count { get { lock (_gate) return _count; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            lock (_gate)
            {
                var completion = NewSignal();
                token.Register(() => completion.TrySetCanceled(token));
                _count++;
                var registered = _registered;
                _registered = NewSignal();
                registered.TrySetResult();
                return completion.Task;
            }
        }

        public async Task WaitForCountAsync(int count)
        {
            while (true)
            {
                Task registered;
                lock (_gate)
                {
                    if (_count >= count) return;
                    registered = _registered.Task;
                }
                await registered.WaitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];
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
        public Action<RunEventAppend>? OnAppend { get; set; }
        public RunEventAppend[] Snapshot() { lock (_gate) return [.. _events]; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(evt);
                OnAppend?.Invoke(evt);
                return Task.FromResult(new RunEventRecord(_events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                    evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId,
                    evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
            }
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
