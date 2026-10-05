using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: policy values and JSON only.
public sealed class ConductorAutonomyPolicyMechanicalReworkTests
{
    private const string OffJson = """
        {"name":"PermissiveCap5Rollback","maxConcurrentPaidWorkers":4,"acceptanceWidth":1,"maxTotalBudget":20.00,"maxCriterionRetries":2,"plannerSampleCount":1,"perProviderBudgetCaps":null,"autoPromoteRiskThreshold":"Broad","cascadeMechanicalReworkCheap":false,"transitionMap":{"Created":"Auto","AwaitingClarification":"Auto","WorkspaceReady":"Auto","Dispatched":"Auto","Running":"Auto","AwaitingVerification":"Auto","Verifying":"Auto","Verified":"Auto","AcceptanceFailed":"Auto","Merged":"Auto","Recorded":"Auto","CleanedUp":"Auto","Failed":"Auto","Blocked":"Auto","AwaitingHumanInput":"Auto"}}
        """;

    [Fact]
    public void VerbatimPolicy_OffMissingDefaultAndRoundTripRemainIndependent()
    {
        var off = ConductorAutonomyPolicy.ParseJson(OffJson);
        Assert.False(off.CascadeMechanicalReworkCheap);
        Assert.True(off.CascadeTesterCheapFirst);
        Assert.True(ConductorAutonomyPolicy.ParseJson(OffJson.Replace("\"cascadeMechanicalReworkCheap\":false,", "")).CascadeMechanicalReworkCheap);
        Assert.True(ConductorAutonomyPolicy.ParseJson(OffJson.Replace("\"cascadeMechanicalReworkCheap\":false", "\"cascadeMechanicalReworkCheap\":null")).CascadeMechanicalReworkCheap);
        Assert.False(ConductorAutonomyPolicy.ParseJson(off.ToJson()).CascadeMechanicalReworkCheap);
        var opposite = off with { CascadeMechanicalReworkCheap = true, CascadeTesterCheapFirst = false };
        var restored = ConductorAutonomyPolicy.ParseJson(opposite.ToJson());
        Assert.True(restored.CascadeMechanicalReworkCheap);
        Assert.False(restored.CascadeTesterCheapFirst);
        Assert.Contains("\"cascadeMechanicalReworkCheap\": false", off.ToJson());
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    public void InvalidSwitch_ThrowsNamedFormatError(string value)
    {
        var json = OffJson.Replace("\"cascadeMechanicalReworkCheap\":false", "\"cascadeMechanicalReworkCheap\":" + value);
        Assert.Contains("cascadeMechanicalReworkCheap", Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json)).Message);
    }
}
