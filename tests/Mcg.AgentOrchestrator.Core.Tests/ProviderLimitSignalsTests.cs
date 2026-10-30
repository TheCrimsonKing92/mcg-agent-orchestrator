using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: parser inputs and completion times are fixed; no shared state or processes.
public sealed class ProviderLimitSignalsTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 5, 10, 12, 56, TimeSpan.Zero);

    [Fact]
    public void AbsoluteResetPreservesBaselineInstant()
    {
        // Baseline 4d1d7767f: CliProviderRefusalClassificationTests.ClaudeLimitUsesAbsoluteReset.
        var verification = Verification(
            stdout: "You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)");

        Assert.True(ProviderLimitSignals.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero), retryAfter);
        Assert.Equal(TimeSpan.Zero, retryAfter.Offset);
    }

    [Fact]
    public void RelativeRetryPreservesBaselineDurationAndInstant()
    {
        // Baseline 4d1d7767f: DispatchOutcomeClassifyTests.ClassifyRecoverableSubscriptionLimitFromStderr.
        // Its 42s input takes the original CompletedAt.Add(retryAfterDelay) return path.
        const string line = "ERROR: Rate limit reached for gpt-5.5. Please try again in 42s."; // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        var verification = Verification(stderr: line);

        Assert.True(ProviderLimitSignals.TryGetRelativeRetryAfter(line, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(42), delay);
        Assert.True(ProviderLimitSignals.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 13, 38, TimeSpan.Zero), retryAfter);
        Assert.Equal(TimeSpan.Zero, retryAfter.Offset);
    }

    [Fact]
    public void BudgetExhaustionPreservesBaselineEvidenceLine()
    {
        // Baseline 4d1d7767f: ProviderBudgetHoldScheduleTests.Exhausted and the unchanged line parser.
        const string line = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
        var verification = Verification(stderr: line);

        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionLine(verification, out var evidence));
        Assert.Equal(line, evidence);
    }

    [Fact]
    public void NonmatchingLinePreservesFalseAndDefaultOutputs()
    {
        const string line = "Inspected the fixture and completed the requested work.";
        var verification = Verification(stdout: line, stderr: line);

        Assert.False(ProviderLimitSignals.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter));
        Assert.Equal(default(DateTimeOffset), retryAfter);
        Assert.False(ProviderLimitSignals.TryGetRelativeRetryAfter(line, out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.False(ProviderLimitSignals.TryGetProviderBudgetExhaustionLine(verification, out var evidence));
        Assert.Equal(string.Empty, evidence);
    }

    private static TaskVerificationRecord Verification(string stdout = "", string stderr = "") =>
        new("opaque command", "C:\\repo", 1, stdout, stderr, CompletedAt);
}
