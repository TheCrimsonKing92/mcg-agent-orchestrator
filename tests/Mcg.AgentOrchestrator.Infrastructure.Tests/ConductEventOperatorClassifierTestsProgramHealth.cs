using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classifier decision table.
public sealed class ConductEventOperatorClassifierTestsProgramHealth
{
    [Theory]
    [InlineData("program-health", "PROGRAM_HEALTH_ALERT conditions=held-receipt", "decision")]
    [InlineData("program-health", "PROGRAM_HEALTH_CLEARED previous=x", "outcome")]
    [InlineData("program-health", "PROGRAM_HEALTH_ALERT_EXTRA", null)]
    [InlineData("program-health", "PROGRAM_HEALTH_CLEARED_EXTRA", null)]
    [InlineData("program-health", " PROGRAM_HEALTH_ALERT conditions=held-receipt", null)]
    [InlineData("program-health", " PROGRAM_HEALTH_CLEARED previous=x", null)]
    [InlineData("other", "PROGRAM_HEALTH_ALERT conditions=held-receipt", null)]
    [InlineData("other", "PROGRAM_HEALTH_CLEARED previous=x", null)]
    public void RequiresPositiveEvidenceOfAnExactTransition(string kind, string detail, string? expected) =>
        Assert.Equal(expected, ConductEventOperatorClassifier.Classify(kind, detail));
}
