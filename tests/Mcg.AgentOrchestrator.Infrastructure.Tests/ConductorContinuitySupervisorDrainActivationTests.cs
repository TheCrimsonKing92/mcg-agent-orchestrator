using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, a private clock, and a unique artifact directory per case.
public sealed class ConductorContinuitySupervisorDrainActivationTests
{
    [Xunit.Fact]
    public async Task FreshDrainPastTickGapThenSelfRelaunchKeepsSuccessorBuild()
    {
        await using var scenario = new Scenario();
        await scenario.StartWithOneTickAsync();
        await scenario.KeepDrainingAsync(TimeSpan.FromMinutes(8));

        scenario.AssertStillActivating();
        Assert.True(scenario.Deadlines.Count >= 4); // Two expired gaps were actually observed.
        scenario.StopSuccessor("self-relaunch-handoff", restart: true);

        Assert.Equal(0, await scenario.Run.WaitAsync(TestContext.Current.CancellationToken));
        scenario.AssertStillActivating();
        Assert.Equal(3, scenario.Host.Requests.Count);
        Assert.Equal(scenario.Host.Requests[1].CommandPrefix,
            scenario.Host.Requests[2].CommandPrefix);
        Assert.Contains(scenario.Events.Snapshot(), evt =>
            evt.Operation == "restart" && evt.Status == "planned" &&
            evt.Detail?.StartsWith("self-relaunch-handoff stdout=", StringComparison.Ordinal) == true);
        Assert.Contains(scenario.ConductLines, line =>
            line.StartsWith("LOOP_HANDOFF ", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.ConductLines, line =>
            line.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task OneHealthyTickWithoutFurtherOutputRevertsAtOriginalGap()
    {
        await using var scenario = new Scenario();
        await scenario.StartWithOneTickAsync();

        scenario.Deadlines.Advance(TimeSpan.FromSeconds(239));
        scenario.AssertStillActivating();
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(1));

        await scenario.AssertTickStallAsync(1);
    }

    [Xunit.Fact]
    public async Task DrainSilenceForFullGapRevertsWithTickStall()
    {
        await using var scenario = new Scenario();
        await scenario.StartWithOneTickAsync();
        await scenario.KeepDrainingAsync(TimeSpan.FromMinutes(3));
        scenario.EmitDrain();
        scenario.Deadlines.Advance(TimeSpan.FromMinutes(1));
        await scenario.WaitForDrainDeadlineAsync(3);

        scenario.Deadlines.Advance(TimeSpan.FromSeconds(179));
        scenario.AssertStillActivating();
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(1));

        await scenario.AssertTickStallAsync(1);
    }

    [Xunit.Fact]
    public async Task ContinuousDrainCannotRenewBudgetFromLastHealthyTick()
    {
        await using var scenario = new Scenario();
        await scenario.StartWithOneTickAsync();
        await scenario.KeepDrainingAsync(scenario.TickStallBudget - TimeSpan.FromSeconds(1));
        scenario.EmitDrain();
        scenario.AssertStillActivating();
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(1));

        await scenario.AssertTickStallAsync(1);
    }

    [Xunit.Fact]
    public async Task DrainBeforeFirstHealthyTickDoesNotExtendStartupStall()
    {
        await using var scenario = new Scenario();
        scenario.Start();
        await scenario.Deadlines.WaitForCountAsync(1);
        scenario.EmitDrain();
        scenario.Deadlines.Advance(TimeSpan.FromMinutes(1));
        scenario.EmitDrain();
        scenario.Deadlines.Advance(TimeSpan.FromMinutes(1));

        await scenario.AssertTickStallAsync(0);
    }

    [Xunit.Fact]
    public async Task NewHealthyTickResetsGapAndDoesNotReuseOlderDrain()
    {
        await using var scenario = new Scenario();
        await scenario.StartWithOneTickAsync();
        await scenario.KeepDrainingAsync(TimeSpan.FromMinutes(8));
        var nextDeadline = scenario.Deadlines.Count + 1;
        scenario.EmitTick(2);
        await scenario.WaitForDrainDeadlineAsync(nextDeadline);
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(239));
        scenario.AssertStillActivating();
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(1));

        await scenario.AssertTickStallAsync(2);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 4, 5, null)]
    [Xunit.InlineData(0, null, 5, null)]
    [Xunit.InlineData(1, 0, 5, null)]
    [Xunit.InlineData(0, 1, 5, null)] // Silence boundary.
    [Xunit.InlineData(0, 9, 10, null)] // Budget boundary despite fresh output.
    [Xunit.InlineData(0, 4, 5, 180)] // Silence expires first.
    [Xunit.InlineData(0, 8, 9, 60)] // Tick budget expires first.
    public void DrainDecisionWaitsOnlyUntilEarlierSilenceOrBudgetBoundary(
        int? tickMinute, int? drainMinute, int nowMinute, int? expectedSeconds)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var wait = ActivationDrainStallDecision.Evaluate(origin.AddMinutes(nowMinute),
            tickMinute is { } tick ? origin.AddMinutes(tick) : null,
            drainMinute is { } drain ? origin.AddMinutes(drain) : null,
            TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10));

        Assert.Equal(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) :
            (TimeSpan?)null, wait);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public readonly RecordingEvents Events = new();
        public readonly VirtualDeadlines Deadlines = new();
        public readonly List<string> ConductLines = [];
        public readonly TimeSpan TickStallBudget = ConductorContinuitySupervisor.ResolveTickStallBudget(null);
        public readonly TaskCompletionSource Reverted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _successorLease = new();
        private readonly CancellationTokenSource _stop =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _successorExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _artifacts = Path.Combine(Path.GetTempPath(),
            $"mcg-drain-activation-{Guid.NewGuid():N}");
        private ConductorSupervisorProcessRequest? _successorRequest;

        public Scenario()
        {
            Events.OnAppend = evt =>
            {
                if (evt.Operation == "activation" && evt.Status == "reverted")
                    Reverted.TrySetResult();
            };
            Host = new ScriptedHost((request, index, token) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                        new("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
                }
                if (index == 1)
                {
                    _successorRequest = request;
                    request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    token.Register(() => _successorExit.TrySetResult(
                        new ConductorSupervisorProcessResult(-1, 101, TerminationConfirmed: true)));
                    return _successorExit.Task;
                }

                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                for (var tick = 1; tick <= 3; tick++)
                    request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new("stop-file", 3, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 102));
            });
        }

        public ScriptedHost Host { get; }
        public Task<int> Run { get; private set; } = Task.FromResult(-1);

        public void Start()
        {
            var successor = new ConductorPreparedSuccessor(
                "C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll",
                "head-next", "head-next", "LOOP_START selfCheck=true", _successorLease);
            var stageCount = 0;
            var supervisor = new ConductorContinuitySupervisor(Host, Events,
                timeProvider: Deadlines, stageSuccessor: _ => stageCount++ == 0 ? successor : null!,
                appendConductEvent: (_, _, detail) => ConductLines.Add(detail),
                activationHealthyTicks: 3, activationDelay: Deadlines.Delay,
                activationStallTimeout: TimeSpan.FromMinutes(2), tickStallBudget: TickStallBudget);
            Run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "120"],
                "C:\\repo", _artifacts, "default", "default", cancellationToken: _stop.Token);
        }

        public async Task StartWithOneTickAsync()
        {
            Start();
            await Deadlines.WaitForCountAsync(1);
            EmitTick(1);
            await Deadlines.WaitForCountAsync(2);
        }

        public async Task KeepDrainingAsync(TimeSpan duration)
        {
            var target = Deadlines.GetUtcNow() + duration;
            while (Deadlines.GetUtcNow() < target)
            {
                EmitDrain();
                var nextDeadline = Deadlines.Count + 1;
                var remaining = target - Deadlines.GetUtcNow();
                if (Deadlines.Advance(remaining < TimeSpan.FromMinutes(1) ? remaining :
                    TimeSpan.FromMinutes(1)))
                {
                    await WaitForDrainDeadlineAsync(nextDeadline);
                }
                AssertStillActivating();
            }
        }

        public async Task WaitForDrainDeadlineAsync(int count)
        {
            var registered = Deadlines.WaitForCountAsync(count);
            await Task.WhenAny(registered, Reverted.Task).WaitAsync(TestContext.Current.CancellationToken);
            AssertStillActivating();
            await registered;
        }

        public void AssertStillActivating()
        {
            Assert.DoesNotContain(Events.Snapshot(), evt =>
                evt.Operation == "activation" && evt.Status is "reverted" or "adopted");
        }

        public async Task AssertTickStallAsync(int tick)
        {
            await Reverted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await Run.WaitAsync(TestContext.Current.CancellationToken));
            var reverted = Assert.Single(Events.Snapshot(), evt =>
                evt.Operation == "activation" && evt.Status == "reverted");
            Assert.Contains("reason=TickStall", reverted.Detail, StringComparison.Ordinal);
            Assert.Contains($"detail=lastHealthyTick={tick}", reverted.Detail, StringComparison.Ordinal);
            Assert.Null(Host.Requests[2].CommandPrefix);
            Assert.Throws<ObjectDisposedException>(() => _successorLease.ReadByte());
        }

        public void EmitTick(int tick) =>
            _successorRequest!.OnStandardOutputLine!($"TICK_END tick={tick} activation=true");

        public void EmitDrain() =>
            _successorRequest!.OnStandardOutputLine!("LOOP_RELAUNCH_DRAIN tick=1 liveGates=1");

        public void StopSuccessor(string reason, bool restart)
        {
            ConductorContinuityExitArtifact.Write(_successorRequest!.ExitArtifactPath,
                new(reason, 1, 1, restart));
            _successorExit.TrySetResult(new ConductorSupervisorProcessResult(0, 101));
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
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _pending = [];
        private TaskCompletionSource _registered = NewSignal();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public int Count { get { lock (_gate) return _pending.Count; } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            lock (_gate)
            {
                var completion = NewSignal();
                token.Register(() => completion.TrySetCanceled(token));
                _pending.Add((_now + duration, completion));
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
                    if (_pending.Count >= count) return;
                    registered = _registered.Task;
                }
                await registered.WaitAsync(TestContext.Current.CancellationToken);
            }
        }

        public bool Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
                var fired = false;
                foreach (var (due, completion) in _pending)
                    if (due <= _now) fired |= completion.TrySetResult();
                return fired;
            }
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken,
            Task<ConductorSupervisorProcessResult>> step) : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken token)
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

        public Task<RunEventRecord> AppendAsync(
            RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(evt);
                OnAppend?.Invoke(evt);
                return Task.FromResult(new RunEventRecord(_events.Count,
                    evt.EventId ?? Guid.NewGuid().ToString("N"),
                    evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId,
                    evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
            }
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
