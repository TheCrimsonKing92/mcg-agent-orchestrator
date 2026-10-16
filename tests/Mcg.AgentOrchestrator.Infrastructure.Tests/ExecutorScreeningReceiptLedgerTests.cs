using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class ExecutorScreeningReceiptLedgerTests
{
    [Fact]
    public void ReceiptRoundTripsEveryFieldAndSkipsAMalformedLine()
    {
        using var fixture = new Fixture();
        Assert.Empty(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        var receipt = new ExecutorScreeningReceipt(Guid.NewGuid().ToString("N"), "goal", "commit", "tree", "main",
            "laptop", fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow().AddMinutes(5), ExecutorScreeningVerdict.Fail,
            [new("lane", "sc-12345678", "hash", RemoteLaneOutcomeCode.RemoteRed, ["A.B"], ["C.D"], 300.25)],
            [new("excluded", "exclusive-key:host:Fixture")]);
        ExecutorScreeningReceiptLedger.Append(fixture.ReceiptPath, receipt);
        File.AppendAllText(fixture.ReceiptPath, "{broken-json\n");
        ExecutorScreeningReceiptLedger.Append(fixture.ReceiptPath, receipt with { ReceiptId = "second" });
        var all = ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath);
        Assert.Equal(2, all.Count);
        var actual = all[0];
        Assert.Equal(receipt with { LaneOutcomes = actual.LaneOutcomes, ExcludedLanes = actual.ExcludedLanes }, actual);
        var expectedLane = Assert.Single(receipt.LaneOutcomes);
        var actualLane = Assert.Single(actual.LaneOutcomes);
        Assert.Equal(expectedLane with { FailingTests = actualLane.FailingTests,
            IgnoredKnownDivergentTests = actualLane.IgnoredKnownDivergentTests }, actualLane);
        Assert.Equal(expectedLane.FailingTests, actualLane.FailingTests);
        Assert.Equal(expectedLane.IgnoredKnownDivergentTests, actualLane.IgnoredKnownDivergentTests);
        Assert.Equal(receipt.ExcludedLanes, actual.ExcludedLanes);
        Assert.Equal("second", all[1].ReceiptId);
        var json = File.ReadAllText(fixture.ReceiptPath);
        Assert.Contains("\"candidate_commit\"", json);
        Assert.Contains("\"remote-red\"", json);
    }

    [Fact]
    public void VerdictRequiresPositiveFailureEvidenceOrCompletePassingCoverage()
    {
        var accepted = new ExecutorScreeningLaneOutcome("A", "sc-a", "hash", RemoteLaneOutcomeCode.Accepted, [], [], 0);
        var failure = accepted with { Outcome = RemoteLaneOutcomeCode.RemoteRed, FailingTests = ["A.B"] };
        var unknown = accepted with { Outcome = RemoteLaneOutcomeCode.LeaseExpired };
        var known = failure with { FailingTests = [], IgnoredKnownDivergentTests = ["A.B"] };
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, ExecutorScreeningReceiptLedger.DecideVerdict([]));
        Assert.Equal(ExecutorScreeningVerdict.Pass, ExecutorScreeningReceiptLedger.DecideVerdict([accepted, known]));
        Assert.Equal(ExecutorScreeningVerdict.Fail, ExecutorScreeningReceiptLedger.DecideVerdict([unknown, failure]));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, ExecutorScreeningReceiptLedger.DecideVerdict([accepted, unknown]));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, ExecutorScreeningReceiptLedger.DecideVerdict(
            [failure with { Outcome = RemoteLaneOutcomeCode.BindingMismatchCommit }]));
    }
}
