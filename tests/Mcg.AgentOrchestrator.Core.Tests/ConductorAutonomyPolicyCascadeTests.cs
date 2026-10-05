using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: policy values and JSON only.
public sealed class ConductorAutonomyPolicyCascadeTests
{
    private const string OffJson = """
        {"name":"PermissiveCap5Rollback","maxConcurrentPaidWorkers":4,"acceptanceWidth":1,"maxTotalBudget":20.00,"maxCriterionRetries":2,"plannerSampleCount":1,"perProviderBudgetCaps":null,"autoPromoteRiskThreshold":"Broad","cascadeTesterCheapFirst":false,"transitionMap":{"Created":"Auto","AwaitingClarification":"Auto","WorkspaceReady":"Auto","Dispatched":"Auto","Running":"Auto","AwaitingVerification":"Auto","Verifying":"Auto","Verified":"Auto","AcceptanceFailed":"Auto","Merged":"Auto","Recorded":"Auto","CleanedUp":"Auto","Failed":"Auto","Blocked":"Auto","AwaitingHumanInput":"Auto"}}
        """;

    [Fact]
    public void VerbatimRollbackPolicy_OffMissingDefaultsAndAliasRoundTrip()
    {
        var off = ConductorAutonomyPolicy.ParseJson(OffJson);
        Assert.False(off.CascadeTesterCheapFirst);
        var missing = ConductorAutonomyPolicy.ParseJson(OffJson.Replace("\"cascadeTesterCheapFirst\":false,", ""));
        Assert.True(missing.CascadeTesterCheapFirst);
        Assert.Equal("gpt-6-luna", missing.CascadeCheapModelAlias);
        var roundTrip = ConductorAutonomyPolicy.ParseJson((off with { CascadeCheapModelAlias = "gpt-6-sol" }).ToJson());
        Assert.False(roundTrip.CascadeTesterCheapFirst);
        Assert.Equal("gpt-6-sol", roundTrip.CascadeCheapModelAlias);
        Assert.Contains("\"cascadeTesterCheapFirst\": false", off.ToJson());
        Assert.Contains("\"cascadeCheapModelAlias\": \"gpt-6-luna\"", off.ToJson());
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("123")]
    public void InvalidAlias_FailsWithNamedKey(string alias)
    {
        var json = OffJson.Replace("\"cascadeTesterCheapFirst\":false,", $"\"cascadeCheapModelAlias\":{alias},");
        Assert.Contains("cascadeCheapModelAlias", Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(json)).Message);
    }
}
