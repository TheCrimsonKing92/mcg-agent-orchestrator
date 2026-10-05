using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSelfHandoffDrainCap : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSelfHandoffDrainCap(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void ProvenDispatchDetachesAtCapAndHandsOff()
    {
        var result = RunScenario(
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", true);

        Assert.Equal(1, result.Detached);
        Assert.Equal(0, result.Cancelled);
        Assert.Equal(TimeSpan.FromMinutes(3), result.DetachAfterLanding);
        Assert.Equal(1, result.Output.Split("LOOP_RELAUNCH_DETACH", StringSplitOptions.None).Length - 1);
        Assert.Contains("detached=1", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.IndexOf("LOOP_RELAUNCH_DETACH", StringComparison.Ordinal) <
                    result.Output.IndexOf("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal));
        Assert.True(result.HandoffStarted);
        Assert.True(result.DetachPersistedBeforeHandoff);
    }

    [Xunit.Fact]
    public void RollbackAllowsSecondLandingToDetachNewDispatch()
    {
        var result = RunScenario(
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", true,
            rollbackFirstHandoff: true);

        Assert.Equal(2, result.Detached);
        Assert.Equal(0, result.Cancelled);
        Assert.Equal(2, result.Output.Split("LOOP_RELAUNCH_DETACH", StringSplitOptions.None).Length - 1);
        Assert.True(result.HandoffStarted);
        Assert.True(result.DetachPersistedBeforeHandoff);
    }

    [Xunit.Fact]
    public void UnprovenDispatchKeepsFullDrainAtCap()
    {
        var result = RunScenario(
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs", false);

        Assert.Equal(0, result.Detached);
        Assert.Equal(0, result.Cancelled);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
        Assert.Contains("active=1 admitting=false reason=identity-unproven", result.Output, StringComparison.Ordinal);
        Assert.True(result.TerminalReceiptBeforeHandoff);
        Assert.True(result.HandoffStarted);
    }

    [Xunit.Fact]
    public void DispatchHandlingLandingKeepsFullDrainAtCap()
    {
        var result = RunScenario(
            "src/Mcg.AgentOrchestrator.Execution/Processes/BackgroundDispatchRunner.cs", true);

        Assert.Equal(0, result.Detached);
        Assert.Equal(0, result.Cancelled);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
        Assert.Contains("active=1 admitting=false reason=dispatch-handling-changed", result.Output, StringComparison.Ordinal);
        Assert.True(result.TerminalReceiptBeforeHandoff);
        Assert.True(result.HandoffStarted);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 3)]
    [Xunit.InlineData("", 3)]
    [Xunit.InlineData("abc", 3)]
    [Xunit.InlineData("0", 3)]
    [Xunit.InlineData("-5", 3)]
    [Xunit.InlineData("2.5", 3)]
    [Xunit.InlineData("2147483648", 3)]
    [Xunit.InlineData("7", 7)]
    public void DrainCapAcceptsOnlyPositiveIntegerMinutes(string? value, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ConductorBatchLoop.ResolveRelaunchDrainCap(value));

    private static ScenarioResult RunScenario(
        string landedPath, bool provenIdentity, bool rollbackFirstHandoff = false)
    {
        var kernel = new AgentOrchestratorKernel();
        CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
        var runningGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
        var runningTask = runningGoal.Tasks.Single();
        var now = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        if (provenIdentity)
        {
            var root = Path.Combine(Path.GetTempPath(), $"mcg-relaunch-cap-{Guid.NewGuid():N}");
            var command = "worker Developer";
            kernel.RecordTaskDispatch(runningGoal.Id, runningTask.Id,
                new TaskDispatchRecord("test-worker", command, root, now, BaseCommit: "abc123"));
            kernel.RecordTaskProcessStarted(runningGoal.Id, runningTask.Id, new TaskProcessRecord(
                111, command, root, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), now, null, null,
                OwnedProcessIds: [111], ProcessIdentityStartedAt: now));
        }
        else StartProcess(kernel, runningGoal, runningTask, now, "abc123");

        var landed = false;
        var landedGoalIds = new HashSet<string>(StringComparer.Ordinal);
        var sleeps = 0;
        var detached = 0;
        var cancelled = 0;
        var handoffAttempts = 0;
        DateTimeOffset? landedAt = null;
        DateTimeOffset? detachedAt = null;
        var order = new List<string>();
        var driver = MakeDriver(
            getFacts: goal => landedGoalIds.Contains(goal.Id.Value)
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: goal =>
            {
                landed = true;
                landedGoalIds.Add(goal.Id.Value);
                landedAt = now;
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ => [landedPath]);

        bool handoffStarted = false;
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var summary = new ConductorBatchLoop(
                utcNow: () => now,
                reapGoalRunningDispatches: (_, _) => cancelled++,
                detachGoalRunningDispatches: (_, goal) =>
                {
                    foreach (var task in goal.Tasks.Where(task => task.LastProcess is
                                 { IsRunning: true, WasGracefullyDetachedByConductor: false }))
                    {
                        kernel.RecordTaskProcessGracefullyDetached(goal.Id, task.Id,
                            task.LastProcess! with { WasGracefullyDetachedByConductor = true });
                        detached++;
                        detachedAt = now;
                    }
                },
                selfRelaunchEnabled: true,
                readRelaunchDrainCap: () => null,
                selfRelaunch: _ =>
                {
                    if (rollbackFirstHandoff && ++handoffAttempts == 1)
                    {
                        CreateVerifiedSimpleGoal(kernel, "Update conductor runtime again");
                        var nextRunningGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                            kernel, DefaultAgents(), "New worker after rollback");
                        var nextTask = nextRunningGoal.Tasks.Single();
                        var root = Path.Combine(Path.GetTempPath(), $"mcg-relaunch-cap-{Guid.NewGuid():N}");
                        const string command = "worker Developer";
                        kernel.RecordTaskDispatch(nextRunningGoal.Id, nextTask.Id,
                            new TaskDispatchRecord("test-worker", command, root, now, BaseCommit: "abc123"));
                        kernel.RecordTaskProcessStarted(nextRunningGoal.Id, nextTask.Id, new TaskProcessRecord(
                            222, command, root, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                            Path.Combine(root, "exit.txt"), now, null, null,
                            OwnedProcessIds: [222], ProcessIdentityStartedAt: now));
                        return new ConductorSelfRelaunchResult(false, "build", "test rollback");
                    }
                    order.Add("handoff");
                    return new ConductorSelfRelaunchResult(true, null, null,
                        ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log"));
                }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 20, watchInterval: TimeSpan.FromMilliseconds(1),
                    persistTick: _ => { if (detached > 0) order.Add($"detach-persisted-{detached}"); },
                    sleepFunc: _ =>
                    {
                        now += TimeSpan.FromMinutes(1);
                        if (landed && ++sleeps == 8)
                        {
                            order.Add("terminal-receipt");
                            CompleteDispatchedTask(kernel, runningGoal, runningTask, now, "def456");
                        }
                        return false;
                    });
            handoffStarted = summary.Handoff?.Started == true;
        });
        return new ScenarioResult(output, detached, cancelled, handoffStarted,
            order.IndexOf("terminal-receipt") >= 0 &&
            order.IndexOf("terminal-receipt") < order.IndexOf("handoff"),
            detachedAt - landedAt,
            order.IndexOf($"detach-persisted-{detached}") >= 0 &&
            order.IndexOf($"detach-persisted-{detached}") < order.IndexOf("handoff"));
    }

    private sealed record ScenarioResult(
        string Output, int Detached, int Cancelled, bool HandoffStarted,
        bool TerminalReceiptBeforeHandoff, TimeSpan? DetachAfterLanding,
        bool DetachPersistedBeforeHandoff);
}
