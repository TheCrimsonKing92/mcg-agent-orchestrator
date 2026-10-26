using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure evidence-state resolution.
public sealed class GoalAcceptanceEvidenceBundleOwnerConsoleTests
{
    [Theory]
    [InlineData("owner console tests", true, "passed")]
    [InlineData("owner console tests", false, "failed")]
    [InlineData("infrastructure tests", true, "missing")]
    [InlineData("storage retention tests", true, "missing")]
    public void FocusedPolicy_ResolvesOnlyOwningAggregate(string aggregateName, bool passed, string expected)
    {
        var check = new VerificationPolicyCheck("focused changed owner console tests", "dotnet-test", true,
            "dotnet test --project " + AcceptancePolicyShardPlanner.OwnerConsoleTestsProject +
            " --verbosity minimal --filter FullyQualifiedName~OwnerConsoleBoardTests", "Changed tests.");
        var state = GoalAcceptanceEvidenceBundleBuilder.ResolvePolicyCheckState(check,
            [new AcceptanceCheckResult(aggregateName, passed, passed ? 0 : 1, null)]);
        Assert.Equal(expected, state);
    }
}
