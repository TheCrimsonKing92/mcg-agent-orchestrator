using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure check identities.
public sealed class AcceptanceIdenticalTreeStorageRetentionPartitionTests
{
    [Fact]
    public void WholeProject_NormalizesIdentityAndNamesPartition()
    {
        var forward = Check(AcceptancePolicyShardPlanner.StorageRetentionTestsProject);
        var backward = Check(AcceptancePolicyShardPlanner.StorageRetentionTestsProject.Replace('/', '\\'));
        Assert.True(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(forward, out var first));
        Assert.True(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(backward, out var second));
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal("storage-retention-tests", AcceptanceIdenticalTreeReuseRule.PartitionId(forward));
        Assert.Equal("storage-retention-tests", AcceptanceIdenticalTreeReuseRule.PartitionId(backward));
        Assert.Null(AcceptanceIdenticalTreeReuseRule.UncacheableReasonCode("storage retention tests"));
        Assert.False(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(
            Check(AcceptancePolicyShardPlanner.ExecutionTestsProject), out _));
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Check(string project) => new()
    {
        Name = "storage retention tests", Type = "dotnet-test", Project = project,
        Arguments = ["--verbosity", "minimal"]
    };
}
