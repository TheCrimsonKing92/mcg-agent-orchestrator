using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopTestsActivationHeartbeatBeforeSleep(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void NoProgressWatchTickEmitsHeartbeatBeforeWatchSleep()
    {
        var lines = RunOneIdleWatchTick(out var sleepInterval);
        var loopStartIndex = Array.FindIndex(lines, line =>
            line.StartsWith("LOOP_START ", StringComparison.Ordinal));
        var heartbeatIndex = Array.FindIndex(lines, line =>
            line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal));
        var watchSleepIndex = Array.FindIndex(lines, line =>
            line.StartsWith("WATCH_SLEEP tick=1 ", StringComparison.Ordinal));
        var sleepEnteredIndex = Array.FindIndex(lines, line => line == "FAKE_SLEEP_ENTERED");

        Assert.True(loopStartIndex >= 0 && loopStartIndex < heartbeatIndex &&
            heartbeatIndex < watchSleepIndex,
            "LOOP_START and the completed tick heartbeat must precede WATCH_SLEEP.");
        Assert.True(watchSleepIndex < sleepEnteredIndex,
            "WATCH_SLEEP must precede the injected sleep.");
        Assert.Single(lines.Where(line =>
            line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal)));
        Assert.True(sleepInterval > TimeSpan.FromMinutes(2));
    }

    [Xunit.Fact]
    public async Task IdleSuccessorWithHeartbeatBeforeWatchSleepIsAdoptedPastStallWindow()
    {
        var events = new RecordingRunEventStore();
        var adopted = NewSignal();
        events.OnAppend = evt =>
        {
            if (evt.Operation == "activation" && evt.Status == "adopted")
                adopted.TrySetResult();
        };
        var deadlineCreated = NewSignal();
        var releaseDeadline = NewSignal();
        var sleepEntered = NewSignal();
        var releaseSleep = NewSignal();
        var (kernel, _) = SimpleGoal();
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "Parked goal keeps the watch summary visible");
        kernel.ParkGoal(parked.Id, "parked");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
        var sleepInterval = TimeSpan.Zero;
        Task<string>? loopOutput = null;
        var successorExit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ConductorSupervisorProcessRequest? successorRequest = null;
        var host = new ScriptedHost((request, index, cancellationToken) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 700));
            }
            if (index == 1)
            {
                successorRequest = request;
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                loopOutput = Task.Run(() =>
                {
                    var writer = new ForwardingCaptureWriter(line => request.OnStandardOutputLine!(line));
                    AsyncLocalConsoleRouter.CaptureTo(() =>
                        new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                            NoStopPath(), maxIterations: 2, watchInterval: TimeSpan.FromMinutes(3),
                            sleepFunc: interval =>
                            {
                                sleepInterval = interval;
                                Console.WriteLine("FAKE_SLEEP_ENTERED");
                                sleepEntered.TrySetResult();
                                releaseSleep.Task.GetAwaiter().GetResult();
                                return true;
                            }, emitActivationHeartbeat: true), writer);
                    return writer.ToString();
                });
                cancellationToken.Register(() => successorExit.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 701, TerminationConfirmed: true)));
                return successorExit.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 702));
        });
        using var lease = new MemoryStream();
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => Build("idle-heartbeat", lease),
            activationHealthyTicks: 1,
            activationStallTimeout: TimeSpan.FromMinutes(2),
            activationDelay: (_, _) =>
            {
                deadlineCreated.TrySetResult();
                return releaseDeadline.Task;
            });

        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-watch-{Guid.NewGuid():N}"),
            "default", "default");
        string[] lines;
        try
        {
            await deadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
            await sleepEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(sleepInterval > TimeSpan.FromMinutes(2));
            releaseDeadline.TrySetResult();
            var outcome = await Task.WhenAny(adopted.Task, run)
                .WaitAsync(TestContext.Current.CancellationToken);
            Assert.Same(adopted.Task, outcome);
        }
        finally
        {
            releaseSleep.TrySetResult();
        }
        lines = (await loopOutput!.WaitAsync(TestContext.Current.CancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ConductorContinuityExitArtifact.Write(successorRequest!.ExitArtifactPath,
            new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
        successorExit.TrySetResult(new ConductorSupervisorProcessResult(0, 701));
        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        var loopStartIndex = Array.FindIndex(lines, line =>
            line.StartsWith("LOOP_START ", StringComparison.Ordinal));
        var heartbeatIndex = Array.FindIndex(lines, line =>
            line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal));
        var watchSleepIndex = Array.FindIndex(lines, line =>
            line.StartsWith("WATCH_SLEEP tick=1 ", StringComparison.Ordinal));
        var sleepEnteredIndex = Array.FindIndex(lines, line => line == "FAKE_SLEEP_ENTERED");
        Assert.True(loopStartIndex >= 0 && loopStartIndex < heartbeatIndex &&
            heartbeatIndex < watchSleepIndex &&
            watchSleepIndex < sleepEnteredIndex);
        Assert.Single(lines.Where(line =>
            line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal)));
        Assert.Single(events.Events.Where(evt => evt.Operation == "activation" && evt.Status == "adopted"));
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
    }

    [Xunit.Fact]
    public async Task SuccessorWhoseFirstTickNeverCompletesIsRevertedWithTickStall()
    {
        var events = new RecordingRunEventStore();
        var deadlineCreated = NewSignal();
        var releaseDeadline = NewSignal();
        var successorExit = new TaskCompletionSource<ConductorSupervisorProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ScriptedHost((request, index, cancellationToken) =>
        {
            if (index == 0)
            {
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 710));
            }
            if (index == 1)
            {
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                cancellationToken.Register(() => successorExit.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 711, TerminationConfirmed: true)));
                return successorExit.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 712));
        });
        using var lease = new MemoryStream();
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => Build("stalled-first-tick", lease),
            activationHealthyTicks: 1,
            activationDelay: (_, _) =>
            {
                deadlineCreated.TrySetResult();
                return releaseDeadline.Task;
            });

        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo",
            Path.Combine(Path.GetTempPath(), $"mcg-activation-stall-{Guid.NewGuid():N}"),
            "default", "default");
        await deadlineCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseDeadline.TrySetResult();
        Assert.Equal(0, await run.WaitAsync(TestContext.Current.CancellationToken));
        var reverted = Assert.Single(events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains("reason=TickStall", reverted.Detail, StringComparison.Ordinal);
        Assert.Contains("lastHealthyTick=0", reverted.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
    }

    private static string[] RunOneIdleWatchTick(out TimeSpan sleepInterval)
    {
        var (kernel, _) = SimpleGoal();
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "Parked goal keeps the watch summary visible");
        kernel.ParkGoal(parked.Id, "parked");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
        var observedInterval = TimeSpan.Zero;
        var outputText = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 2, watchInterval: TimeSpan.FromMinutes(3),
                sleepFunc: interval =>
                {
                    observedInterval = interval;
                    Console.WriteLine("FAKE_SLEEP_ENTERED");
                    return true;
                }, emitActivationHeartbeat: true));
        sleepInterval = observedInterval;
        return outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ForwardingCaptureWriter(Action<string> onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null)
                onLine(value);
        }
    }

    private static ConductorPreparedSuccessor Build(string name, IDisposable lease) =>
        new($"C:\\{name}", $"C:\\{name}\\Mcg.AgentOrchestrator.App.dll", name, name,
            "LOOP_START selfCheck=true", lease);

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, cancellationToken);
        }
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public Action<RunEventAppend>? OnAppend { get; set; }

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            OnAppend?.Invoke(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation,
                evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
