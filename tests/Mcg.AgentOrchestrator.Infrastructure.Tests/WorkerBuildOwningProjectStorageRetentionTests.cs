using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: read-only ownership resolution against the verified worktree.
public sealed class WorkerBuildOwningProjectStorageRetentionTests
{
    private const string MovedPath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/StorageRetention/StorageRetentionResumeCursorTests.cs";

    [Fact]
    public void MixedSources_RequireBothCompilingProjects()
    {
        var resolution = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(
            InfrastructureTestSupport.FindRepositoryRoot(),
            [MovedPath, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProfileTests.cs"]);
        Assert.Empty(resolution.Errors);
        Assert.Equal(new[] { AcceptancePolicyShardPlanner.StorageRetentionTestsProject,
            AcceptancePolicyShardPlanner.InfrastructureTestsProject }.Order(StringComparer.Ordinal), resolution.Projects);
    }

    [Fact]
    public void MovedSource_RequiresOnlyNestedProject()
    {
        var resolution = WorkerBuildEvidenceRequirement.ResolveRequiredProjects(
            InfrastructureTestSupport.FindRepositoryRoot(), [MovedPath]);
        Assert.Empty(resolution.Errors);
        Assert.Equal([AcceptancePolicyShardPlanner.StorageRetentionTestsProject], resolution.Projects);
        Assert.DoesNotContain(AcceptancePolicyShardPlanner.InfrastructureTestsProject, resolution.Projects);
    }
}
