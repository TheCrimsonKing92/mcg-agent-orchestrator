using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure evidence-state resolution.
public sealed class GoalAcceptanceEvidenceBundleStorageRetentionTests
{
    [Theory]
    [InlineData("storage retention tests", true, "passed")]
    [InlineData("storage retention tests", false, "failed")]
    [InlineData("infrastructure tests", true, "missing")]
    public void FocusedPolicy_ResolvesOnlyOwningAggregate(string aggregateName, bool passed, string expected)
    {
        var check = new VerificationPolicyCheck("focused changed storage retention tests", "dotnet-test", true,
            "dotnet test --project " + AcceptancePolicyShardPlanner.StorageRetentionTestsProject +
            " --verbosity minimal --filter FullyQualifiedName~StorageRetentionResumeCursorTests", "Changed tests.");
        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(check,
            [new AcceptanceCheckResult(aggregateName, passed, passed ? 0 : 1, null)]);
        Assert.Equal(expected, state);
    }
}
