using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaneEarlyStopTests
{
    [Xunit.Fact]
    public void Snapshot_ReportsPartialCoverageAndEveryIncompleteLane()
    {
        var stop = new AcceptanceLaneEarlyStop("confirming lane", ["pending local", "pending remote"], ["running local"], ["running remote"]);
        var receipt = stop.ToReceiptCheck();
        Assert.Equal(4, stop.StoppedCount);
        Assert.False(receipt.Passed);
        Assert.False(receipt.Advisory);
        Assert.Equal(AcceptanceLaneEarlyStop.ReceiptName, receipt.Name);
        Assert.Equal("lane-early-stop confirming_lane=\"confirming lane\" coverage=partial lanes_stopped=4 " +
            "not_run=[\"pending local\",\"pending remote\"] cancelled_mid_run=[\"running local\"] remote_left_to_finish=[\"running remote\"]", receipt.ResultSummary);
        Assert.Equal(receipt.ResultSummary, receipt.OutputTail);
        Assert.Null(receipt.FailingTestIdentities);
    }

    [Xunit.Fact]
    public void EmptySnapshot_StillFailsAndRendersAllThreeEmptyLists()
    {
        var stop = new AcceptanceLaneEarlyStop("last lane", [], [], []);
        Assert.Equal(0, stop.StoppedCount);
        Assert.False(stop.ToReceiptCheck().Passed);
        Assert.Contains("coverage=partial lanes_stopped=0 not_run=[] cancelled_mid_run=[] remote_left_to_finish=[]", stop.Summary);
    }
}
