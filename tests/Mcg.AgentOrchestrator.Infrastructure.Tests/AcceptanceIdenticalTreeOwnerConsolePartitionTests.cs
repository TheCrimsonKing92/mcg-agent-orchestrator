using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure check identities.
public sealed class AcceptanceIdenticalTreeOwnerConsolePartitionTests
{
    [Fact]
    public void WholeProject_NormalizesIdentityAndNamesPartition()
    {
        var forward = Check(AcceptancePolicyShardPlanner.OwnerConsoleTestsProject);
        var backward = Check(AcceptancePolicyShardPlanner.OwnerConsoleTestsProject.Replace('/', '\\'));
        Assert.True(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(forward, out var first));
        Assert.True(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(backward, out var second));
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
        Assert.Equal("owner-console-tests", AcceptanceIdenticalTreeReuseRule.PartitionId(forward));
        Assert.Equal("owner-console-tests", AcceptanceIdenticalTreeReuseRule.PartitionId(backward));
        Assert.Null(AcceptanceIdenticalTreeReuseRule.UncacheableReasonCode("owner console tests"));
        Assert.False(AcceptanceIdenticalTreeReuseRule.TryGetCheckIdentity(
            Check(AcceptancePolicyShardPlanner.ExecutionTestsProject), out _));
    }

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Check(string project) => new()
    {
        Name = "owner console tests", Type = "dotnet-test", Project = project,
        Arguments = ["--verbosity", "minimal"]
    };
}
