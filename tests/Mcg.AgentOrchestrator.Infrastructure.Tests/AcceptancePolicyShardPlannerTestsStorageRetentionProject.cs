using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable policy inputs and read-only manifest inspection.
public sealed class AcceptancePolicyShardPlannerTestsStorageRetentionProject
{
    private static readonly AcceptanceShardPolicySwitches Scoped = new(FullShards: false, ChangeScoped: true);
    private const string MovedPath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/StorageRetentionResumeCursorTests.cs";

    [Fact]
    public void NestedSource_OwnsOnlyItsProject()
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([MovedPath], Scoped);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.StartsWith("changed projects: Infrastructure.StorageRetention.Tests;", plan.Evidence, StringComparison.Ordinal);
        Assert.Equal([AcceptancePolicyShardPlanner.StorageRetentionTestsProject], plan.DependencyClosure);
        Assert.False(plan.IncludesProject(AcceptancePolicyShardPlanner.InfrastructureTestsProject));
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.Core/Probe.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Probe.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Probe.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Probe.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Probe.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Probe.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.TestSupport/Probe.cs")]
    public void DependencySource_ReachesConsumer(string path)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path], Scoped);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.True(plan.IncludesProject(AcceptancePolicyShardPlanner.StorageRetentionTestsProject));
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Execution/WorkerShellTests.cs")]
    public void UnrelatedTestSource_ExcludesConsumer(string path)
    {
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([path], Scoped);
        Assert.True(plan.Applies);
        Assert.False(plan.ForceFull);
        Assert.False(plan.IncludesProject(AcceptancePolicyShardPlanner.StorageRetentionTestsProject));
    }

    [Fact]
    public void CheckedInChecks_SelectOnlyNestedOwner()
    {
        var checks = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).DeclaredDotnetTestChecks;
        var plan = AcceptancePolicyShardPlanner.BuildPolicyShardPlan([MovedPath], Scoped);
        var selected = Assert.Single(checks.Where(check => check.Name == "storage retention tests"));
        Assert.False(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(selected, plan));
        foreach (var name in new[] { "infrastructure tests", "core tests", "cli tests", "execution tests", "acceptance execution owner tests" })
        {
            var check = Assert.Single(checks.Where(check => check.Name == name));
            Assert.True(AcceptancePolicyShardPlanner.IsSkippedPolicyShardCheck(check, plan), name);
        }
    }
}
