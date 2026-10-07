using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: decisions use immutable facts; the missing receipt path is unique and never written.
public sealed class AcceptanceShardCompletionAdjudicatorRuleTests
{
    [Fact]
    public void PolicyFailureWinsOverInterruptionAndTrxFailure()
    {
        var trx = new TrxCompletionEvidence(0, 0, 0, "Failed", false, "malformed-trx");
        var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(
            true, 2, trx, "policy-failure", "other-signal", false, true);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            false, "policy-failure", true, 2, 0, 0, "Failed", "policy-failure", 0), decision);
    }

    [Fact]
    public void InterruptionWinsOverTrxFailure()
    {
        var trx = new TrxCompletionEvidence(null, null, null, "missing", false, "missing-trx");
        var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(
            true, 2, trx, null, null, false, true);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            false, "timed-out", true, 2, null, null, "missing"), decision);
    }

    [Theory]
    [InlineData(true, false, "missing-trx", null)]
    [InlineData(false, true, null, "missing-trx-injected-runner-compatibility")]
    public void MissingTrxUsesTheEvidenceRequirement(
        bool requireTrx, bool passed, string? predicate, string? signal)
    {
        var trx = new TrxCompletionEvidence(null, null, null, "missing", false, "missing-trx");
        var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(
            false, 0, trx, null, null, false, requireTrx);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            passed, predicate, false, 0, null, null, "missing", signal), decision);
    }

    [Theory]
    [InlineData(0, 0, 0, false, false, 2, false, "zero-tests")]
    [InlineData(5, 3, 1, false, false, 2, false, "incomplete-execution")]
    [InlineData(5, 4, 1, false, true, 2, false, "assembly-cleanup-failure")]
    [InlineData(5, 4, 1, false, false, 2, false, "failing-trx")]
    [InlineData(5, 4, 1, true, false, 1, false, "nonzero-exit")]
    [InlineData(5, 4, 1, true, false, 1, true, null)]
    [InlineData(5, 4, 1, true, false, 0, false, null)]
    public void TestShardPreservesPredicateOrderAndCounters(
        int discovered, int executed, int notExecuted, bool trxPassed,
        bool cleanupOnly, int exitCode, bool allowNonzeroExit, string? predicate)
    {
        var trx = new TrxCompletionEvidence(
            discovered, executed, notExecuted, "receipt-outcome", trxPassed, null, cleanupOnly);
        var decision = AcceptanceShardCompletionAdjudicator.DecideTestShard(
            false, exitCode, trx, null, "policy-signal", allowNonzeroExit, true);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            predicate is null, predicate, false, exitCode, discovered, executed,
            "receipt-outcome", "policy-signal", notExecuted), decision);
    }

    [Theory]
    [InlineData(true, 2, false, "timed-out")]
    [InlineData(false, 2, false, "nonzero-exit")]
    [InlineData(false, 0, true, null)]
    public void NonTestCommandPreservesCompletion(bool interrupted, int exitCode, bool passed, string? predicate)
    {
        var decision = AcceptanceShardCompletionAdjudicator.DecideNonTestCommand(interrupted, exitCode);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            passed, predicate, interrupted, exitCode, null, null, "not-applicable"), decision);
    }

    [Fact]
    public void PassingPartitionKeepsItsPolicySignalAndCounters()
    {
        var result = new AcceptanceCheckResult(
            "acceptance-check-timeout:partition", true, 2, null,
            FailureClassification: "classification", ExecutedTestCount: 4, DiscoveredTestCount: 5);

        var decision = AcceptanceShardCompletionAdjudicator.InferPartition(result, true);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            true, null, false, 2, 5, 4, "not-applicable", "classification"), decision);
    }

    [Theory]
    [InlineData("acceptance-check-timeout:partition", true, 2, "classification", "classification", false)]
    [InlineData("acceptance-check-timeout:partition", false, 0, null, "timed-out", true)]
    [InlineData("partition", true, 0, null, "timed-out", true)]
    [InlineData("partition", false, 2, null, "nonzero-exit", false)]
    [InlineData("partition", false, 0, null, "check-failed", false)]
    public void FailedPartitionPreservesPredicateOrder(
        string name, bool captureLimitFailureName, int exitCode, string? classification,
        string predicate, bool timedOut)
    {
        var result = new AcceptanceCheckResult(
            name, false, exitCode, null,
            FailureClassification: classification, ExecutedTestCount: 4, DiscoveredTestCount: 5);

        var decision = AcceptanceShardCompletionAdjudicator.InferPartition(result, captureLimitFailureName);

        Assert.Equal(new AcceptanceShardCompletionDecision(
            false, predicate, timedOut, exitCode, 5, 4, "not-available", classification), decision);
    }

    [Fact]
    public void MissingReceiptPathsHaveMissingEvidence()
    {
        var expected = new TrxCompletionEvidence(null, null, null, "missing", false, "missing-trx");
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".trx");

        Assert.Equal(expected, AcceptanceShardCompletionAdjudicator.InspectTrxCompletion(null));
        Assert.Equal(expected, AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([]));
        Assert.Equal(expected, AcceptanceShardCompletionAdjudicator.InspectTrxCompletion([missingPath]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TelemetryForwardsBothInterruptionFacts(bool timedOut, bool captureLimited)
    {
        var result = new GoalAcceptanceVerifier.CommandResult(
            0, "", TimedOut: timedOut, CaptureLimited: captureLimited);

        var decision = GoalAcceptanceVerifierTestTelemetry.DecideNonTestCommandCompletion(result);

        Assert.Equal(timedOut || captureLimited, GoalAcceptanceVerifier.IsInterrupted(result));
        Assert.Equal(GoalAcceptanceVerifier.IsInterrupted(result), decision.TimedOut);
        Assert.Equal(new AcceptanceShardCompletionDecision(
            !(timedOut || captureLimited), timedOut || captureLimited ? "timed-out" : null,
            timedOut || captureLimited, 0, null, null, "not-applicable"), decision);
    }
}
