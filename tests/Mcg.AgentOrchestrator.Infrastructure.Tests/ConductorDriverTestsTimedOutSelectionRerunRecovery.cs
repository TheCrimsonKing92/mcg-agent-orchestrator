using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ConductorDriverTestsTimedOutSelectionRerun.Scenario;

// Private artifact roots and injected process, runner and clock seams; parallel-safe.
public sealed class ConductorDriverTestsTimedOutSelectionRerunRecovery
{
    private const string CandidateSha = "abc1234";
    private const string Request = "Infrastructure.Tests:MtpTestRunnerScriptTests";
    private const string Reason = FailedGoalTimedOutSelectionRerunRule.ReasonSlug;

    [Fact]
    public void MissingSelectionMapping_EscalatesWithSeparateDeclineNoteAndDoesNotRun()
    {
        using var scenario = new Scenario(missingSelectionMapping: true);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var note = Assert.Single(scenario.Goal.Timeline.Where(item => item.TaskId == scenario.Tester.Id &&
            item.Kind == ProgressKind.TaskNote && item.Message.StartsWith(Reason + "; declined:", StringComparison.Ordinal)));
        Assert.Contains("selection mapping unavailable", note.Message);
        Assert.Contains("stayed verification-inconclusive on unchanged inputs", Assert.Single(scenario.Escalations));
        Assert.Empty(scenario.RunRequests);
        Assert.Empty(scenario.Dispatches);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void TesterOwnedTimeout_AttachesReceiptToInconclusiveVerificationAndDispatchesOnce()
    {
        using var scenario = new Scenario(testerOwnsReceipt: true);
        Assert.Same(scenario.Tester, scenario.Owner);
        Assert.Equal(WorkTaskStatus.Failed, scenario.Owner.Status);
        var originalRound = scenario.Tester.LastVerification!;
        Assert.Single(originalRound.FindingEvidenceReceipts!);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Tester.VerificationHistory
            .SelectMany(round => round.FindingEvidenceReceipts ?? [])
            .DistinctBy(item => item.ReceiptId).Where(item => item.FindingRoundFingerprint == Reason));
        Assert.True(receipt.Passed);
        var updatedRound = Assert.Single(scenario.Tester.VerificationHistory.Where(round =>
            round.HasSameRoundIdentity(originalRound)));
        Assert.Contains(receipt, updatedRound.FindingEvidenceReceipts!);
        var inputs = scenario.Tester.LastDispatch!.InconclusiveRoundInputs!;
        Assert.Contains(receipt.ReceiptId, inputs.ReceiptIds);
        Assert.NotEqual(scenario.OriginalInputs, inputs);
        Assert.Empty(scenario.Escalations);
    }

    [Fact]
    public void EarlierCandidateReceipt_CurrentTimeoutRerunsAndDispatchesOnce()
    {
        using var scenario = new Scenario(earlierCandidateReceipt: true);
        Assert.Contains("earlier-receipt", scenario.OriginalInputs.ReceiptIds);
        Assert.Contains("original-receipt", scenario.OriginalInputs.ReceiptIds);
        var earlier = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(receipt => receipt.ReceiptId == "earlier-receipt"));
        Assert.True(earlier.Passed);
        Assert.NotEqual(CandidateSha, earlier.CandidateSha);

        var result = scenario.Driver().AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.FindingRoundFingerprint == Reason));
        Assert.True(receipt.Passed);
        Assert.Contains(receipt.ReceiptId, scenario.Tester.LastDispatch!.InconclusiveRoundInputs!.ReceiptIds);
        Assert.Empty(scenario.Escalations);
    }

    [Theory(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    [InlineData(nameof(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot))]
    [InlineData(nameof(ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable))]
    public void CapacityDeferral_RelaunchRetriesBatchThenDispatchesTesterOnce(
        string outcome)
    {
        using var scenario = new Scenario();
        var blockedOutcome = Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcome);
        var launches = 0;
        var blockSlot = blockedOutcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot;
        ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(scenario.AttemptsRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: pid => pid == 7103,
            launchOwnedProcess: _ => { launches++; return new(7103); },
            acquireStableSlotLease: (_, _) => blockSlot
                ? throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy("fixture", []))
                : null);
        var coordinator = Coordinator();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver(coordinator: coordinator)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        var first = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(1, launches);
        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0, [], CandidateSha, null);
        coordinator.RunAttemptForTests(first, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, _) =>
            {
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable, blockedOutcome);
                return ConductorParallelAcceptanceRunResult.Fault(current,
                    new AcceptanceInfrastructureDeferredException("structural-coverage-permit-unavailable", null,
                        "fixture permit unavailable"));
            });
        var blocked = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(blockedOutcome, blocked.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver(coordinator: coordinator)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Empty(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Empty(scenario.RunRequests);
        Assert.Empty(scenario.Escalations);
        Assert.Empty(scenario.Dispatches);

        // The request and reconciled deferral are durable; no driver memory grants the relaunch.
        blockSlot = false;
        scenario.Reload();
        coordinator = Coordinator();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver(coordinator: coordinator)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        var second = Assert.Single(coordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]));
        Assert.Equal(2, launches);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal(first.FocusedEvidenceBatchId, second.FocusedEvidenceBatchId);
        Assert.Equal(first.FocusedEvidenceRequest, second.FocusedEvidenceRequest);
        Assert.Equal(first.CandidateKey, second.CandidateKey);
        Assert.Equal(Request, second.FocusedEvidenceRequest);
        coordinator.RunAttemptForTests(second, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, _) =>
            {
                scenario.RunRequests.Add(Request);
                return ConductorParallelAcceptanceRunResult.Focused(current, new(Request, true, true,
                    "fixture passed evidence", [new AcceptanceCheckResult("focused rerun", true, 0, "passed")]));
            });
        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Driver(coordinator: coordinator)
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(new[] { Request }, scenario.RunRequests);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Retries);
        Assert.Equal(new[] { scenario.Tester.Id }, scenario.Dispatches);
        var receipt = Assert.Single(scenario.Owner.LastVerification!.FindingEvidenceReceipts!
            .Where(item => item.FindingRoundFingerprint == Reason));
        Assert.True(receipt.Passed);
        Assert.Contains(receipt.ReceiptId, scenario.Tester.LastDispatch!.InconclusiveRoundInputs!.ReceiptIds);
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal)));
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            item.Message.StartsWith(Reason, StringComparison.Ordinal)));
        Assert.Empty(scenario.Escalations);

        scenario.CompleteTester();
        scenario.RetryAndCompleteTester();
        scenario.Reload();
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver(coordinator: Coordinator())
            .AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(2, launches);
        Assert.Single(scenario.RunRequests);
        Assert.Single(scenario.Dispatches);
    }
}
