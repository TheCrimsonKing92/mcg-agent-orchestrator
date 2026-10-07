using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: explicit immutable policy inputs; no process environment changes.
public sealed class AcceptancePolicyShardPlannerTestsExecutionTestsProject
{
    private static readonly AcceptanceShardPolicySwitches ScopedSwitches =
        new(FullShards: false, ChangeScoped: true);

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/CliCommandDispatcher.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs")]
    public void HostSource_ExcludesExecutionTests(string path)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path], ScopedSwitches);

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.False(plan.IncludesProject(AcceptancePolicyShardPlanner.ExecutionTestsProject));
        Assert.True(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(ExecutionCheck(), plan));
    }

    [Fact]
    public void ExecutionSource_IncludesExecutionTests()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["src/Mcg.AgentOrchestrator.Execution/Persistence/OperatorIntentStore.cs"], ScopedSwitches);

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.True(plan.IncludesProject(AcceptancePolicyShardPlanner.ExecutionTestsProject));
        Assert.False(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(ExecutionCheck(), plan));
    }

    [Fact]
    public void ExecutionTestSource_NamesNestedProject()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan(
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Execution/PracticeRegistryStoreTests.cs"],
            ScopedSwitches);

        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.Equal([AcceptancePolicyShardPlanner.ExecutionTestsProject], plan.DependencyClosure);
        Assert.StartsWith("changed projects: Infrastructure.Execution.Tests;", plan.Evidence,
            StringComparison.Ordinal);
        Assert.False(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(ExecutionCheck(), plan));
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck ExecutionCheck() => new()
    {
        Name = "execution tests",
        Type = "dotnet-test",
        Project = AcceptancePolicyShardPlanner.ExecutionTestsProject
    };
}
