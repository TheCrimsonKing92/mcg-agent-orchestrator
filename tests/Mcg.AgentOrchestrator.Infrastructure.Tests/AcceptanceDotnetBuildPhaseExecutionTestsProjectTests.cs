using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: pure build-plan inputs, an injected SHA resolver, and no filesystem writes.
public sealed class AcceptanceDotnetBuildPhaseExecutionTestsProjectTests
{
    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/CliCommandDispatcher.cs", false)]
    [InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs", false)]
    [InlineData("src/Mcg.AgentOrchestrator.Execution/Persistence/OperatorIntentStore.cs", true)]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Execution/PracticeRegistryStoreTests.cs", true)]
    public void CachePlan_BuildsExecutionTestsOnlyForTheirDependencyClosure(string changedPath, bool buildsExecution)
    {
        var changedFiles = new[] { changedPath };
        var shardPlan = BuildPolicyShardPlan(changedFiles,
            new AcceptanceShardPolicySwitches(FullShards: false, ChangeScoped: true));
        var phase = AcceptanceDotnetBuildPhase.Create(
            Path.Combine(Path.GetTempPath(), "execution-build-plan"),
            [new() { Type = "dotnet-test", Project = InfrastructureTestsProject },
             new() { Type = "dotnet-test", Project = ExecutionTestsProject }],
            changedFiles, shardPlan, _ => "abc1234");

        var cachePlan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        Assert.Contains(ExecutionTestsProject, cachePlan.CacheableProjects);
        Assert.Equal(buildsExecution, cachePlan.BuildProjects.Contains(ExecutionTestsProject));
        Assert.Equal(!buildsExecution, cachePlan.RestoreProjects.Contains(ExecutionTestsProject));
    }

    [Fact]
    public void CachePlan_OmitsExecutionTestsWhenNoCheckTargetsThem()
    {
        string[] changedFiles = ["src/Mcg.AgentOrchestrator.Core/Application/Goal.cs"];
        var phase = AcceptanceDotnetBuildPhase.Create(
            Path.Combine(Path.GetTempPath(), "execution-build-plan"),
            [new() { Type = "dotnet-test", Project = InfrastructureTestsProject }],
            changedFiles, PolicyShardPlan.Scoped("focused closure",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InfrastructureTestsProject, CoreProject }),
            _ => "abc1234");

        var cachePlan = Assert.IsType<DotnetBaseBuildCachePlan>(phase.CachePlan);
        Assert.DoesNotContain(ExecutionTestsProject, cachePlan.CacheableProjects);
        Assert.DoesNotContain(ExecutionTestsProject, cachePlan.BuildProjects);
        Assert.DoesNotContain(ExecutionTestsProject, cachePlan.RestoreProjects);
    }
}
