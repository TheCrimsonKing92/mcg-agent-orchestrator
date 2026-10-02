using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopTestsActivationHeartbeatBeforeSleepOffTickCadence(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public Task BlockedBackgroundCadenceAllowsSuccessorAdoption() => RunActivationAsync(offTick: true);

    [Xunit.Fact]
    public Task SynchronousCadenceRevertsSuccessorWithZeroHealthyTicks() => RunActivationAsync(offTick: false);

    private static async Task RunActivationAsync(bool offTick)
    {
        // Same ScriptedHost/supervisor/real-loop seam as the existing activation-heartbeat family.
        var root = Path.Combine(Path.GetTempPath(), $"mcg-activation-cadence-{Guid.NewGuid():N}");
        var events = new RecordingRunEventStore();
        var adopted = NewSignal();
        events.OnAppend = evt =>
        {
            if (evt.Operation == "activation" && evt.Status == "adopted") adopted.TrySetResult();
        };
        var deadlineCreated = NewSignal();
        var releaseDeadline = NewSignal();
        var cadenceEntered = NewSignal();
        var releaseCadence = NewSignal();
        var cadenceReturned = NewSignal();
        var sleepEntered = NewSignal();
        var releaseSleep = NewSignal();
        var lines = new ConcurrentQueue<string>();
        void BlockingCadence()
        {
            cadenceEntered.TrySetResult();
            TestHangGuard.WaitAsync(releaseCadence.Task, "activation cadence release").GetAwaiter().GetResult();
            cadenceReturned.TrySetResult();
        }
        var runner = new RunEventMaintenanceCadenceRunner(Path.Combine(root, "conduct-events.log"), BlockingCadence);
        var (kernel, _) = SimpleGoal();
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "Parked goal keeps the watch summary visible");
        kernel.ParkGoal(parked.Id, "parked");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);
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
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 720));
            }
            if (index == 1)
            {
                successorRequest = request;
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                loopOutput = Task.Run(async () =>
                {
                    // Hold startup until the supervisor is observing; avoid racing deadline creation.
                    await TestHangGuard.WaitAsync(deadlineCreated.Task, "activation observer ready");
                    var writer = new ForwardingCaptureWriter(line =>
                    {
                        lines.Enqueue(line);
                        request.OnStandardOutputLine!(line);
                    });
                    AsyncLocalConsoleRouter.CaptureTo(() =>
                        new ConductorBatchLoop(sweep: _ =>
                        {
                            if (offTick) runner.OnTick();
                            else BlockingCadence();
                        }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                            NoStopPath(), maxIterations: 2, watchInterval: TimeSpan.FromMinutes(3),
                            sleepFunc: _ =>
                            {
                                sleepEntered.TrySetResult();
                                TestHangGuard.WaitAsync(releaseSleep.Task, "activation watch sleep release")
                                    .GetAwaiter().GetResult();
                                return true;
                            }, emitActivationHeartbeat: true), writer);
                    return writer.ToString();
                });
                cancellationToken.Register(() => successorExit.TrySetResult(
                    new ConductorSupervisorProcessResult(-1, 721, TerminationConfirmed: true)));
                return successorExit.Task;
            }

            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 722));
        });
        using var lease = new MemoryStream();
        using var supervisorCancellation = new CancellationTokenSource();
        var supervisor = new ConductorContinuitySupervisor(host, events,
            stageSuccessor: _ => Build("cadence-heartbeat", lease),
            activationHealthyTicks: 1,
            activationStallTimeout: TimeSpan.FromMinutes(2),
            activationDelay: (_, _) =>
            {
                deadlineCreated.TrySetResult();
                return releaseDeadline.Task;
            });
        var run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", root,
            "default", "default", cancellationToken: supervisorCancellation.Token);
        try
        {
            await TestHangGuard.WaitAsync(cadenceEntered.Task, "activation cadence entered");
            if (offTick)
            {
                await TestHangGuard.WaitAsync(sleepEntered.Task, "successor reached watch sleep");
                releaseDeadline.TrySetResult();
                var outcome = await TestHangGuard.WaitAsync(Task.WhenAny(adopted.Task, run),
                    "successor adoption outcome");
                Assert.Same(adopted.Task, outcome);
                Assert.Single(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
                Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
                Assert.Contains(lines, line => line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal));
                Assert.False(releaseCadence.Task.IsCompleted);
                Assert.False(cadenceReturned.Task.IsCompleted);
                Assert.False(runner.CurrentRun!.IsCompleted);

                // LOOP_STOP can complete while the cadence is still held.
                releaseSleep.TrySetResult();
                await TestHangGuard.WaitAsync(loopOutput!, "loop stop with cadence still blocked");
                Assert.False(cadenceReturned.Task.IsCompleted);
                ConductorContinuityExitArtifact.Write(successorRequest!.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
                successorExit.TrySetResult(new ConductorSupervisorProcessResult(0, 721));
                Assert.Equal(0, await TestHangGuard.WaitAsync(run, "adopted supervisor stop"));
            }
            else
            {
                // Cadence entry follows the real LOOP_START, so expiration must mean TickStall.
                releaseDeadline.TrySetResult();
                Assert.Equal(0, await TestHangGuard.WaitAsync(run, "synchronous cadence activation revert"));
                var reverted = Assert.Single(events.Events, evt => evt.Operation == "activation" && evt.Status == "reverted");
                Assert.Contains("reason=TickStall", reverted.Detail, StringComparison.Ordinal);
                Assert.Contains("lastHealthyTick=0", reverted.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted");
                Assert.DoesNotContain(lines, line => line.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal));
                Assert.False(cadenceReturned.Task.IsCompleted);
            }
        }
        finally
        {
            releaseCadence.TrySetResult();
            releaseSleep.TrySetResult();
            releaseDeadline.TrySetResult();
            supervisorCancellation.Cancel();
            successorExit.TrySetResult(new ConductorSupervisorProcessResult(-1, 721, TerminationConfirmed: true));
            try
            {
                if (loopOutput is not null)
                    await TestHangGuard.WaitAsync(loopOutput, "activation loop cleanup");
                if (runner.CurrentRun is { } cadence)
                    await TestHangGuard.WaitAsync(cadence, "activation background cadence cleanup");
                try { await TestHangGuard.WaitAsync(run, "activation supervisor cleanup"); }
                catch (OperationCanceledException) when (supervisorCancellation.IsCancellationRequested) { }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ForwardingCaptureWriter(Action<string> onLine) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null) onLine(value);
        }
    }

    private static ConductorPreparedSuccessor Build(string name, IDisposable lease) =>
        new($"C:\\{name}", $"C:\\{name}\\Mcg.AgentOrchestrator.App.dll", name, name,
            "LOOP_START selfCheck=true", lease);

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        private int _requests;
        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken) =>
            step(request, _requests++, cancellationToken);
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public ConcurrentQueue<RunEventAppend> Events { get; } = new();
        public Action<RunEventAppend>? OnAppend { get; set; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(evt);
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
