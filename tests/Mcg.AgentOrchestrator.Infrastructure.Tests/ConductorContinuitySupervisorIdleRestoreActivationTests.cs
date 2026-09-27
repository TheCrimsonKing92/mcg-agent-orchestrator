using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorContinuitySupervisorIdleRestoreActivationTests
{
    [Xunit.Fact]
    public async Task IdleCandidateWithHeartbeatBeforeSleepIsAdopted()
    {
        var scenario = new Scenario(restore: false);
        Assert.Equal(0, await scenario.RunIdleSequence());
        Assert.Equal(2, scenario.Host.Requests.Count);
        Assert.Contains(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "adopted");
        Assert.DoesNotContain(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "reverted");
    }

    [Xunit.Fact]
    public async Task IdleRestoredBuildWithHeartbeatBeforeSleepIsRestored()
    {
        var scenario = new Scenario(restore: true);
        Assert.Equal(0, await scenario.RunIdleSequence());
        Assert.Equal(3, scenario.Host.Requests.Count);
        Assert.Null(scenario.Host.Requests[2].CommandPrefix);
        Assert.Contains(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "reverted");
        Assert.Contains(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "restored");
        Assert.DoesNotContain(scenario.Events.Events, evt =>
            evt.Operation == "activation" && evt.Status == "failed-both");
        Assert.Empty(scenario.Attention);
    }

    private static async Task AwaitSignalAsync(Task signal, string name)
    {
        try
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out awaiting {name}.");
        }
    }

    private sealed class Scenario(bool restore)
    {
        public readonly RecordingEvents Events = new();
        public readonly VirtualDeadlines Deadlines = new();
        public readonly List<string> Attention = [];
        private readonly MemoryStream _successorLease = new();
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _idleExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _adopted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _idleLaunched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ConductorSupervisorProcessRequest? _idleRequest;

        public ScriptedHost Host { get; } = new((_, _, _) =>
            throw new InvalidOperationException("Scenario host was not initialized."));

        public async Task<int> RunIdleSequence()
        {
            Events.OnAppend = evt =>
            {
                if (evt.Operation == "activation" && evt.Status == "adopted")
                    _adopted.TrySetResult();
            };
            Host.Step = (request, index, token) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                        new("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
                }
                if (restore && index == 1)
                {
                    request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    return Task.FromResult(new ConductorSupervisorProcessResult(-1, 101));
                }

                _idleRequest = request;
                _idleLaunched.TrySetResult();
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                request.OnStandardOutputLine("TICK_END tick=1 activation=true");
                request.OnStandardOutputLine("IDLE_SLEEP seconds=120");
                token.Register(() => _idleExit.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 102, TerminationConfirmed: true)));
                return _idleExit.Task;
            };

            var successor = new ConductorPreparedSuccessor(
                "C:\\next", "C:\\next\\Mcg.AgentOrchestrator.App.dll",
                "head-next", "head-next", "LOOP_START selfCheck=true", _successorLease);
            var supervisor = new ConductorContinuitySupervisor(Host, Events,
                timeProvider: Deadlines,
                stageSuccessor: _ => successor, activationHealthyTicks: 3,
                activationDelay: Deadlines.Delay,
                activationStallTimeout: TimeSpan.FromMinutes(2),
                raiseActivationAttention: Attention.Add);
            var run = supervisor.RunAsync(["conduct", "--loop", "--daemon", "--poll-seconds", "120"],
                "C:\\repo", Path.Combine(Path.GetTempPath(), $"mcg-idle-restore-{Guid.NewGuid():N}"),
                "default", "default");

            await AwaitSignalAsync(_idleLaunched.Task, restore ? "restored child launch" : "candidate child launch");
            await Deadlines.WaitForCountAsync(1);
            Assert.Equal(TimeSpan.FromMinutes(4), Deadlines.LastDuration);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                Deadlines.Advance(TimeSpan.FromSeconds(120));
                _idleRequest!.OnStandardOutputLine!($"TICK_END tick={iteration + 2} activation=true");
                if (iteration == 0)
                {
                    _idleRequest.OnStandardOutputLine("IDLE_SLEEP seconds=120");
                    await Deadlines.WaitForCountAsync(2);
                }
            }
            if (!restore)
                await AwaitSignalAsync(_adopted.Task, "ACTIVATION_ADOPTED");
            ConductorContinuityExitArtifact.Write(_idleRequest!.ExitArtifactPath,
                new("stop-file", 3, 0, false));
            _idleExit.TrySetResult(new ConductorSupervisorProcessResult(0, restore ? 102 : 101));
            try
            {
                return await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("Timed out awaiting supervisor run completion.");
            }
        }
    }

    private sealed class VirtualDeadlines : TimeProvider
    {
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _pending = [];
        private readonly List<(int Count, TaskCompletionSource Completion)> _registrations = [];
        private DateTimeOffset _now = new(2026, 9, 27, 6, 0, 0, TimeSpan.Zero);

        public TimeSpan LastDuration { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;

        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            LastDuration = duration;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => completion.TrySetCanceled(token));
            _pending.Add((_now + duration, completion));
            foreach (var registration in _registrations)
                if (_pending.Count >= registration.Count) registration.Completion.TrySetResult();
            return completion.Task;
        }

        public Task WaitForCountAsync(int count)
        {
            if (_pending.Count >= count) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrations.Add((count, completion));
            return AwaitSignalAsync(completion.Task, $"delay registration count {count}");
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
        public Func<ConductorSupervisorProcessRequest, int, CancellationToken,
            Task<ConductorSupervisorProcessResult>> Step { get; set; } = step;
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return Step(request, index, token);
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
