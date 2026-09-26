using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorIdleActivationTests
{
    [Xunit.Fact]
    public async Task IdleSuccessorSleepingFullWatchIntervalBetweenTicksIsAdopted()
    {
        var scenario = new Scenario();
        var run = scenario.Start();
        await scenario.Deadlines.WaitForCountAsync(1);
        scenario.EmitTick(1);
        await scenario.Deadlines.WaitForCountAsync(2);
        scenario.EmitTick(2);
        await scenario.Deadlines.WaitForCountAsync(3);

        Assert.Equal(TimeSpan.FromMinutes(4), scenario.Deadlines.LastDuration);
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(125));
        scenario.EmitTick(3);
        await scenario.Adopted.Task.WaitAsync(TestContext.Current.CancellationToken);
        scenario.StopSuccessor();

        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, scenario.Host.Requests.Count);
        Assert.DoesNotContain(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "reverted");
    }

    [Xunit.Fact]
    public async Task SuccessorSilentPastWatchSleepPlusAllowanceIsRevertedWithTickStall()
    {
        var scenario = new Scenario();
        var run = scenario.Start();
        await scenario.Deadlines.WaitForCountAsync(1);
        scenario.EmitTick(1);
        await scenario.Deadlines.WaitForCountAsync(2);
        scenario.EmitTick(2);
        await scenario.Deadlines.WaitForCountAsync(3);

        Assert.Equal(TimeSpan.FromMinutes(4), scenario.Deadlines.LastDuration);
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(239));
        Assert.DoesNotContain(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "reverted");
        scenario.Deadlines.Advance(TimeSpan.FromSeconds(2));
        await scenario.Reverted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        var reverted = Assert.Single(scenario.Events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains("reason=TickStall", reverted.Detail, StringComparison.Ordinal);
        Assert.Contains("lastHealthyTick=2", reverted.Detail, StringComparison.Ordinal);
        Assert.Null(scenario.Host.Requests[2].CommandPrefix);
        Assert.Throws<ObjectDisposedException>(() => scenario.SuccessorLease.ReadByte());
    }

    private sealed class Scenario
    {
        public readonly RecordingEvents Events = new();
        public readonly VirtualDeadlines Deadlines = new();
        public readonly MemoryStream SuccessorLease = new();
        public readonly TaskCompletionSource Adopted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Reverted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _successorExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConductorSupervisorProcessRequest? _successorRequest;

        public Scenario()
        {
            Events.OnAppend = evt =>
            {
                if (evt.Operation == "activation" && evt.Status == "adopted")
                    Adopted.TrySetResult();
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

                Assert.Null(request.CommandPrefix);
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

        public Task<int> Start()
        {
            var successor = new ConductorPreparedSuccessor(
                "C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll",
                "head-next", "head-next", "LOOP_START selfCheck=true", SuccessorLease);
            var supervisor = new ConductorContinuitySupervisor(Host, Events,
                timeProvider: Deadlines, stageSuccessor: _ => successor,
                activationHealthyTicks: 3, activationDelay: Deadlines.Delay,
                activationStallTimeout: TimeSpan.FromMinutes(2));
            return supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "120"],
                "C:\\repo", Path.Combine(Path.GetTempPath(), $"mcg-idle-activation-{Guid.NewGuid():N}"),
                "default", "default");
        }

        public void EmitTick(int tick) =>
            _successorRequest!.OnStandardOutputLine!($"TICK_END tick={tick} activation=true");

        public void StopSuccessor()
        {
            ConductorContinuityExitArtifact.Write(_successorRequest!.ExitArtifactPath,
                new("stop-file", 3, 0, false));
            _successorExit.TrySetResult(new ConductorSupervisorProcessResult(0, 101));
        }
    }

    private sealed class VirtualDeadlines : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _pending = [];
        private readonly List<TaskCompletionSource> _registrations = [];
        private DateTimeOffset _now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

        public TimeSpan LastDuration { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            LastDuration = duration;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            _pending.Add((_now + duration, completion));
            if (_registrations.Count > 0)
                _registrations[^1].TrySetResult();
            return completion.Task;
        }

        public Task WaitForCountAsync(int count)
        {
            if (_pending.Count >= count) return Task.CompletedTask;
            var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrations.Add(registration);
            return registration.Task.WaitAsync(TestContext.Current.CancellationToken);
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var (due, completion) in _pending)
                if (due <= _now) completion.TrySetResult();
        }
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
        public List<RunEventAppend> Events { get; } = [];
        public Action<RunEventAppend>? OnAppend { get; set; }

        public Task<RunEventRecord> AppendAsync(
            RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            OnAppend?.Invoke(evt);
            return Task.FromResult(new RunEventRecord(Events.Count,
                evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId,
                evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
