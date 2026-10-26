using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: read-only ownership resolution against the verified worktree.
public sealed class WorkerBuildOwningProjectOwnerConsoleTests
{
    private const string MovedPath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OwnerConsole/OwnerConsoleBoardTests.cs";

    [Fact]
    public void MixedSources_RequireBothCompilingProjects()
    {
        var resolution = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(
            InfrastructureTestSupport.FindRepositoryRoot(),
            [MovedPath, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);
        Assert.Empty(resolution.Errors);
        Assert.Equal(new[] { AcceptancePolicyShardPlanner.OwnerConsoleTestsProject,
            AcceptancePolicyShardPlanner.InfrastructureTestsProject }.Order(StringComparer.Ordinal), resolution.Projects);
    }

    [Fact]
    public void MovedSource_RequiresOnlyNestedProject()
    {
        var resolution = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(
            InfrastructureTestSupport.FindRepositoryRoot(), [MovedPath]);
        Assert.Empty(resolution.Errors);
        Assert.Equal([AcceptancePolicyShardPlanner.OwnerConsoleTestsProject], resolution.Projects);
        Assert.DoesNotContain(AcceptancePolicyShardPlanner.InfrastructureTestsProject, resolution.Projects);
    }
}
