using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classifier decision table.
public sealed class ConductEventOperatorClassifierTestsInvariantBreach
{
    [Theory]
    [InlineData("invariant-breach", "INVARIANT_BREACH kind=x", "decision")]
    [InlineData("invariant-breach", "INVARIANT_CLEARED kind=x", "outcome")]
    [InlineData("invariant-breach", " INVARIANT_BREACH kind=x", null)]
    [InlineData("invariant-breach", " INVARIANT_CLEARED kind=x", null)]
    [InlineData("invariant-breach", "INVARIANT_BREACH_EXTRA kind=x", null)]
    [InlineData("invariant-breach", "INVARIANT_CLEARED_EXTRA kind=x", null)]
    [InlineData("other", "INVARIANT_BREACH kind=x", null)]
    [InlineData("other", "INVARIANT_CLEARED kind=x", null)]
    public void Classify_ExactTransitionToken_ReturnsExpectedClass(string kind, string detail, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(kind, detail));
}
