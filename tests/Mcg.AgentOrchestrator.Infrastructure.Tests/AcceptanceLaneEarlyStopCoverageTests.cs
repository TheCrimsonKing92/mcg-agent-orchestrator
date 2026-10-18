using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable receipt data only; no shared resources.
public sealed class AcceptanceLaneEarlyStopCoverageTests
{
    [Fact]
    public void Receipt_ConfirmingLane_CoversFailureAndPreservesSnapshotFields()
    {
        var snapshot = new AcceptanceLaneEarlyStop("lane a", ["p"], ["c"], ["r"]);

        var receipt = snapshot.ToReceiptCheck();

        Assert.Equal(new[] { "lane a" }, receipt.CoveredBy);
        Assert.Equal(AcceptanceLaneEarlyStop.ReceiptName, receipt.Name);
        Assert.Equal(snapshot.Summary, receipt.ResultSummary);
        Assert.Equal(snapshot.Summary, receipt.OutputTail);
        Assert.False(receipt.Passed);
        Assert.Equal(1, receipt.ExitCode);
        Assert.False(receipt.Advisory);
        Assert.Null(receipt.FailingTestIdentities);
    }
}
