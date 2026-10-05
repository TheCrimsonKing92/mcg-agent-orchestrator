using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTestsTimedOutSelectionRerun;

// Shared scenario uses a unique artifact root, injected clock and fake runner; parallel-safe.
public sealed class ConductorDriverTestsTimedOutBatchedSelectionRerun
{
    [Fact]
    public void BatchedTimeout_RerunsEveryCoveredTargetOnceAndExcludesPassingTargetsAcrossRelaunch()
    {
        using var scenario = new Scenario(batchedSelection: true);
        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var request = Assert.Single(scenario.RunRequests);
        Assert.Equal(new[] { "Infrastructure.Tests:MtpTestRunnerScriptTests", "Infrastructure.Tests:SecondTimedOutTests" },
            request.Split(';', StringSplitOptions.TrimEntries));
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.ReceiptId != "original-receipt"));
        Assert.True(receipt.Passed);
        Assert.Equal(new[] { "MtpTestRunnerScriptTests", "SecondTimedOutTests" },
            receipt.Request.Selections.Select(item => item.TestClass));
        Assert.Contains(receipt.ReceiptId, scenario.Tester.LastDispatch!.InconclusiveRoundInputs!.ReceiptIds);
        Assert.NotEqual(scenario.OriginalInputs, scenario.Tester.LastDispatch.InconclusiveRoundInputs);

        const string reason = FailedGoalTimedOutSelectionRerunRule.ReasonSlug;
        const string selection = "reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-mtptestrunnerscripttests-fullyqualifiedname-secondtimedouttests";
        var requestEvent = Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Kind == ProgressKind.FindingEvidenceRequestRecorded && item.Message.StartsWith(reason, StringComparison.Ordinal)));
        Assert.Contains("selections=" + selection, requestEvent.Message, StringComparison.Ordinal);
        var receiptEvent = Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Kind == ProgressKind.FindingEvidenceRunRecorded && item.Message.StartsWith(reason, StringComparison.Ordinal)));
        Assert.Contains("receipt_id=" + receipt.ReceiptId, receiptEvent.Message, StringComparison.Ordinal);
        Assert.Contains("outcome=passed", receiptEvent.Message, StringComparison.Ordinal);

        scenario.CompleteTester();
        scenario.RetryAndCompleteTester();
        Assert.True(TesterInconclusiveRoundInputsReader.Read(scenario.Goal, scenario.Tester)!.InputsUnchanged);
        scenario.Reload();
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Single(scenario.RunRequests);
        Assert.Single(scenario.Retries);
        Assert.Single(scenario.Dispatches);
        Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Kind == ProgressKind.FindingEvidenceRequestRecorded && item.Message.StartsWith(reason, StringComparison.Ordinal)));
        Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Kind == ProgressKind.FindingEvidenceRunRecorded && item.Message.StartsWith(reason, StringComparison.Ordinal)));
    }
}
