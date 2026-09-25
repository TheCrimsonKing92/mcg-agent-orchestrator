using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NoReadyBatchHoldTextTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void PreflightReasonPrecedesSerializedTasksInEmptyBatchHold()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Develop", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Explain an empty dispatch batch", [developer, tester, reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Display the first blocker in full.",
            ["The preflight reason precedes serialized tasks."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var preflightReason = $"blocked: Task '{developer.Id.Value}' already has passing verification; retry the task before dispatching it again";
        var preflight = new ReadyBlockedDiagnostic(goal.Id.Value[..8], 1, developer.Id.Value, "codex-cli", "preflight-blocked", [preflightReason]);
        var serialized = new[]
        {
            new ReadyBlockedDiagnostic(goal.Id.Value[..8], 2, tester.Id.Value, "codex-cli", "parallel-serialized", ["waits for dependency completion"]),
            new ReadyBlockedDiagnostic(goal.Id.Value[..8], 3, reviewer.Id.Value, "claude-cli", "parallel-serialized", ["waits for dependency completion"])
        };
        var ordered = GoalDispatchOperations.OrderReadyBlockedDiagnostics([preflight], serialized);
        Assert.Equal([developer.Id.Value, tester.Id.Value, reviewer.Id.Value], ordered.Select(diagnostic => diagnostic.TaskId));

        var batchPlan = new ProcessBatchPlan(goal.Id, goal.Objective, goal.Status, ProcessBatchActionKind.StartDispatches, 0, 3, []);
        var result = new SubscriptionStartResult(
            [],
            new ProcessBatchExecutionResult(batchPlan, []),
            new ParallelExecutionPlan([], []),
            ordered);
        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);
        new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => outcome),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        var blocker = Assert.IsType<GoalHoldState>(kernel.GetGoal(goal.Id).CurrentHold).Blocker;
        var preflightText = $"reason=preflight-blocked: {preflightReason}";
        Assert.StartsWith(ConductorDriver.NoReadyBatchHoldPrefix, blocker, StringComparison.Ordinal);
        Assert.Contains(preflightText, blocker, StringComparison.Ordinal);
        Assert.True(
            blocker.IndexOf(preflightText, StringComparison.Ordinal) < blocker.IndexOf("parallel-serialized", StringComparison.Ordinal),
            $"Expected the unabridged Developer preflight reason before serialized entries: {blocker}");
    }

    [Fact]
    public void SeveralPreflightBlocksKeepTaskIndexOrderBeforeSerializedEntries()
    {
        var later = new ReadyBlockedDiagnostic("goal", 3, "later", "codex-cli", "preflight-blocked", ["later reason"]);
        var earlier = new ReadyBlockedDiagnostic("goal", 1, "earlier", "codex-cli", "preflight-blocked", ["earlier reason"]);
        var serialized = new ReadyBlockedDiagnostic("goal", 2, "serialized", "codex-cli", "parallel-serialized");

        var ordered = GoalDispatchOperations.OrderReadyBlockedDiagnostics([later, earlier], [serialized]);
        Assert.Equal(["earlier", "later", "serialized"], ordered.Select(diagnostic => diagnostic.TaskId));
        var text = ConductorDriver.DescribeEmptyBatch(new ParallelExecutionPlan([], []), ordered);
        Assert.Contains("earlier reason", text, StringComparison.Ordinal);
        Assert.Contains("later reason", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("later reason", StringComparison.Ordinal) < text.IndexOf("parallel-serialized", StringComparison.Ordinal));
    }
}
