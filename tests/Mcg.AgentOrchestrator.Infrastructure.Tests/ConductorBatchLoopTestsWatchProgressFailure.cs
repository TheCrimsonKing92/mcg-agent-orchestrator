using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsWatchProgressFailure(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void OneWatchFailureStillEmitsOtherGoalAndTickSummary()
    {
        var kernel = new AgentOrchestratorKernel();
        var good = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "good watch goal");
        var bad = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "bad watch goal");
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        Start(good, 101);
        Start(bad, 202);

        var reporter = new ConductorWatchProgressReporter(
            readHeartbeat: (process, observedAt) => process.ProcessId == 202
                ? throw new InvalidOperationException("watch unavailable")
                : new DispatchHeartbeatStatus("heartbeat.json", true, null, process.ProcessId, null,
                    [101], "running", observedAt, observedAt, TimeSpan.Zero, TimeSpan.Zero, 0, 0)
                { OwnedProcessIdentities = [Identity(101)] },
            readChanges: (_, _) => new DispatchLiveChangeSnapshot([], [], 0),
            isProcessAlive: pid => pid == 101,
            readProcessIdentity: Identity,
            now: () => now.AddMinutes(1),
            loopProcessId: 9001,
            launcherProcessId: 9002);

        var text = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(watchProgressReporter: reporter).Run(
                kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => true));

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failed = Assert.Single(lines, line => line.StartsWith("WATCH_PROGRESS_FAILED ", StringComparison.Ordinal));
        Assert.Contains($"goal={bad.Id.Value[..8]}", failed);
        Assert.Contains("exception=InvalidOperationException", failed);
        Assert.Contains(lines, line => line.StartsWith($"WATCH_PROGRESS goal={good.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("TICK tick=1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("reason=unintended-exit", StringComparison.Ordinal));

        void Start(Goal goal, int pid)
        {
            var task = goal.Tasks.Single();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", "run", "C:\\repo", now));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(pid, "run", "C:\\repo", "worker.out.log", "worker.err.log",
                    "worker.exit.txt", now, null, null, OwnedProcessIds: [pid]));
        }
    }

    private static SpawnProcessIdentity Identity(int pid) =>
        new(pid, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero), $@"C:\workers\{pid}.exe");
}
