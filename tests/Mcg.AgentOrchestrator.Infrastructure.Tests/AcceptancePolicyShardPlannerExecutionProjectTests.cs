using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable per-test policy switches; no process environment changes.
public sealed class AcceptancePolicyShardPlannerExecutionProjectTests
{
    [Fact]
    public void ExecutionSource_MapsToProjectAndAllInfrastructureConsumers()
    {
        var switches = new AcceptanceShardPolicySwitches(ReadVariable: _ => null);
        var execution = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["src/Mcg.AgentOrchestrator.Execution/Persistence/OperatorIntentStore.cs"], switches);
        var infrastructure = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs"], switches);

        Assert.True(execution.Applies);
        Assert.Contains(AcceptancePolicyShardPlanner.ExecutionProject, execution.DependencyClosure);
        Assert.Contains("changed projects: Execution;", execution.Evidence, StringComparison.Ordinal);
        Assert.Contains(AcceptancePolicyShardPlanner.InfrastructureProject, execution.DependencyClosure);
        Assert.True(infrastructure.Applies);
        Assert.NotEmpty(infrastructure.DependencyClosure);
        foreach (var consumer in infrastructure.DependencyClosure)
            Assert.Contains(consumer, execution.DependencyClosure);
    }
}
