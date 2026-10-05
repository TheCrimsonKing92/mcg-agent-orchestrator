using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: classifier has no mutable state.
public sealed class StateLogDivergenceOperatorClassifierTests
{
    [Theory]
    [InlineData("lost=1 repeated=0", "decision")]
    [InlineData("lost=0 repeated=2", "decision")]
    [InlineData("lost=0 repeated=0 stored_only=3", "outcome")]
    [InlineData("lost=0 repeated=unknown", null)]
    [InlineData("lost=unknown repeated=0", null)]
    [InlineData("lost=-1 repeated=0", null)]
    [InlineData("lost=0 lost=2 repeated=0", null)]
    [InlineData("lost=0", null)]
    [InlineData("lost=10 repeated=unknown", "decision")]
    public void TagsOnlyPositiveDecisionEvidenceOrKnownZeroCounts(string counts, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify("state-log-divergence", "STATE_LOG_DIVERGENCE goal=abcd1234 " + counts));

    [Theory]
    [InlineData("state-log-divergence", "STATE_LOG_DIVERGENCE_SKIPPED lost=1 repeated=0")]
    [InlineData("state-log-divergence-skipped", "STATE_LOG_DIVERGENCE lost=1 repeated=0")]
    [InlineData("state-log-divergence", "OTHER lost=1 repeated=0")]
    public void UnrelatedTokensAndEventsHaveNoOperatorTag(string kind, string detail) =>
        Assert.Null(ConductEventOperatorClassifier.Classify(kind, detail));
}
