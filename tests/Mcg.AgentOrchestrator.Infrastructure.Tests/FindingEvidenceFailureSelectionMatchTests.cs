using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure selection matching; no shared state or external resources.
public sealed class FindingEvidenceFailureSelectionMatchTests
{
    [Fact]
    public void PrefixedAndUnionSelectionsMatchTheirFailures()
    {
        foreach (var selection in new[] { "FullyQualifiedName~AlphaTestsQueue", "AlphaTestsQueue" })
        {
            Assert.True(FindingEvidenceFailureSelectionMatch.Matches(
                "Fixture.AlphaTestsQueue.Method(outcome: Interrupted)", selection));
            Assert.True(FindingEvidenceFailureSelectionMatch.Matches("AlphaTestsQueue.Other", selection));
        }
        Assert.True(FindingEvidenceFailureSelectionMatch.Matches("BetaTests.Case",
            "FullyQualifiedName~AlphaTestsQueue|FullyQualifiedName~BetaTests"));
        Assert.True(FindingEvidenceFailureSelectionMatch.Matches("Fixture.GammaTests.GammaMethod(x: 1)",
            "FullyQualifiedName~GammaMethod"));
    }

    [Fact]
    public void BoundaryNegativeControls()
    {
        Assert.False(FindingEvidenceFailureSelectionMatch.Matches("AlphaTestsQueue.Method",
            "FullyQualifiedName~AlphaTests"));
        Assert.False(FindingEvidenceFailureSelectionMatch.Matches("BetaTests.AlphaTestsQueueHelper",
            "FullyQualifiedName~AlphaTestsQueue"));
        const string opaque = "FullyQualifiedName~AlphaTests&Category!=Slow";
        var baseline = FindingEvidenceFailureSelectionMatch.MatchesName("AlphaTests.Method", opaque.Trim());
        Assert.False(baseline);
        Assert.Equal(baseline, FindingEvidenceFailureSelectionMatch.Matches("AlphaTests.Method", opaque));
        Assert.False(FindingEvidenceFailureSelectionMatch.Matches("Fixture.NotAlphaTests.Method", "AlphaTests"));
        foreach (var identity in new[] { "AlphaTests", "Fixture+AlphaTests+Nested.Case", "AlphaTests[0]" })
            Assert.True(FindingEvidenceFailureSelectionMatch.Matches(identity, " FullyQualifiedName~AlphaTests "));
    }
}
