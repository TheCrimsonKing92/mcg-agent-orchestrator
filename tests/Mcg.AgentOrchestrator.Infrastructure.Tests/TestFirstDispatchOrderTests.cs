using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: scheduling uses an in-memory kernel and no live processes.
public sealed class TestFirstDispatchOrderTests
{
    [Fact]
    public void TestFirst_ImplementerWaitsForAuthorCompletion()
    {
        var (kernel, goal) = CreateGoal("test-first");
        var author = goal.Tasks[1];
        var implementer = goal.Tasks[2];
        var agents = AgentCatalog.Default().Agents;

        var selection = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, agents);
        Assert.Equal(author.Id, Assert.Single(selection.TaskIds));
        Assert.Equal([author.Id.Value], DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, implementer));
        var decision = Assert.Single(selection.Plan.Decisions, item => item.IntentId == implementer.Id.Value);
        Assert.Equal(ParallelExecutionDisposition.Serialized, decision.Disposition);
        Assert.Contains(decision.Reasons, reason => reason.Contains("waits for dependency completion", StringComparison.Ordinal));

        ConductorDriverTests.PassVerification(kernel, goal, author);
        selection = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, agents);
        Assert.Equal(implementer.Id, Assert.Single(selection.TaskIds));
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, implementer));
    }

    [Fact]
    public void ScoutDecision_DoesNotAddSameStageDependency()
    {
        var (_, goal) = CreateGoal("scout");
        var implementer = goal.Tasks[2];
        Assert.Empty(DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, implementer));
        var selection = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, AgentCatalog.Default().Agents);
        var decision = Assert.Single(selection.Plan.Decisions, item => item.IntentId == implementer.Id.Value);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("waits for dependency completion", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(string workflow)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Implement src/Widget.cs with focused tests.",
        [
            new(TaskId.New(), "Plan", AgentRole.Planner),
            new(TaskId.New(), "Test author", AgentRole.Developer),
            new(TaskId.New(), "Implement", AgentRole.Developer),
            new(TaskId.New(), "Review", AgentRole.Reviewer)
        ]);
        kernel.RecordGoalPolicyDecision(goal.Id, $"Intake pipeline decision (override): {workflow}; test fixture.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[0]);
        return (kernel, goal);
    }
}
