using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure marker formatting and parsing.
public sealed class CascadeRouteMarkerTests
{
    [Theory]
    [InlineData("cheap")]
    [InlineData("escalated")]
    [InlineData("primary")]
    public void RoundTrip_PreservesPrefixRuleAndReservedIds(string decision)
    {
        var marker = new CascadeRouteMarker(decision, "tester-cheap-first", ["alpha-receipt", "beta"]);
        var reason = marker.AppendTo("provider-constrained: Tester remains on OpenAI");
        Assert.Equal($"provider-constrained: Tester remains on OpenAI; cascade={decision} rule=tester-cheap-first ids=alpha-receipt,beta", reason);
        Assert.True(CascadeRouteMarker.TryParse(reason, out var parsed));
        Assert.Equal(decision, parsed.Decision);
        Assert.Equal(marker.RuleId, parsed.RuleId);
        Assert.Equal(marker.Ids, parsed.Ids);
        Assert.True(CascadeRouteMarker.TryParse(new CascadeRouteMarker(decision, "a").Format(), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reason without a marker")]
    [InlineData("cascade=unknown rule=a")]
    [InlineData("cascade=cheap rule=Bad")]
    [InlineData("cascade=cheap rule=a_b")]
    [InlineData("cascade=cheap rule=")]
    [InlineData("reason cascade=cheap rule=a")]
    [InlineData("cascade=cheap rule=a ids=x,")]
    [InlineData("cascade=cheap rule=a trailing")]
    public void MalformedMarker_IsNotRouteEvidence(string? reason) =>
        Assert.False(CascadeRouteMarker.TryParse(reason, out _));

    [Theory]
    [InlineData("unknown", "a")]
    [InlineData("cheap", "Bad")]
    [InlineData("primary", "")]
    public void Format_RejectsInvalidDecisionOrRule(string decision, string rule) =>
        Assert.Throws<ArgumentException>(() => new CascadeRouteMarker(decision, rule).Format());
}
