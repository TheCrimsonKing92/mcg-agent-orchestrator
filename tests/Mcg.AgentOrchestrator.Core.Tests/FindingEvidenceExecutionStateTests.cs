using Mcg.AgentOrchestrator.Core;

public sealed class FindingEvidenceExecutionStateTests
{
    private const string CandidateSha = "abc1234";
    private const string OldCandidateSha = "0ldc0de";
    private static readonly FindingEvidenceRequest Request =
        new([new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]);

    [Xunit.Fact(DisplayName = "Finding_without_evidence_request_is_writable_source_work")]
    public void FindingWithoutEvidenceRequestIsWritableSourceWork()
    {
        var task = TaskWithReceipts();

        Assert.Equal(
            FindingEvidenceExecutionState.NoneRequested,
            FindingEvidenceExecutionClassifier.Classify(task, FindingWithoutRequest(), CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Requested_finding_without_any_receipt_is_pending_execution")]
    public void RequestedFindingWithoutAnyReceiptIsPendingExecution()
    {
        var task = TaskWithReceipts();

        Assert.Equal(
            FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Receipt_at_an_older_candidate_cannot_satisfy_the_current_candidate")]
    public void ReceiptAtAnOlderCandidateCannotSatisfyTheCurrentCandidate()
    {
        var receipt = Receipt("old-receipt", OldCandidateSha, executed: true);
        var task = TaskWithReceipts(receipt);
        var finding = Finding(outcome: new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: "old-receipt",
            ResultReason: FindingEvidenceOutcomeReason.ValidEvidence));

        Assert.Equal(
            FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, finding, CandidateSha));
    }

    [Xunit.Theory(DisplayName = "Receipt_at_the_current_candidate_is_executed_whatever_its_verdict")]
    [Xunit.InlineData(true, FindingEvidenceOutcomeReason.ValidEvidence)]
    [Xunit.InlineData(false, FindingEvidenceOutcomeReason.CandidateRed)]
    [Xunit.InlineData(false, FindingEvidenceOutcomeReason.ApparatusFailure)]
    public void ReceiptAtTheCurrentCandidateIsExecutedWhateverItsVerdict(
        bool honoured,
        FindingEvidenceOutcomeReason resultReason)
    {
        var receipt = Receipt("current-receipt", CandidateSha, executed: true);
        var task = TaskWithReceipts(receipt);
        var finding = Finding(outcome: new FindingEvidenceOutcome(
            honoured, ReceiptId: "current-receipt", ResultReason: resultReason));

        Assert.Equal(
            FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(task, finding, CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Zero_test_apparatus_receipt_at_the_current_candidate_is_executed_not_passed")]
    public void ZeroTestApparatusReceiptAtTheCurrentCandidateIsExecutedNotPassed()
    {
        var receipt = Receipt("apparatus-receipt", CandidateSha, executed: true, passed: false);
        var task = TaskWithReceipts(receipt);
        var finding = Finding(outcome: new FindingEvidenceOutcome(
            Honoured: false,
            ReceiptId: "apparatus-receipt",
            Reason: FindingEvidenceNotHonouredReason.SelectionApparatusFailure,
            Detail: "focused selection matched zero tests",
            ResultReason: FindingEvidenceOutcomeReason.ApparatusFailure));

        // A receipt-bearing apparatus failure is not a permanent refusal and is not a pass: it is
        // an executed attempt at this candidate, so the blocker goes back to the writable owner.
        Assert.False(FindingEvidenceExecutionClassifier.IsPermanentRefusal(finding.EvidenceOutcome!));
        Assert.Equal(
            FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(task, finding, CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Receipt_deferring_this_finding_to_a_later_batch_stays_pending")]
    public void ReceiptDeferringThisFindingToALaterBatchStaysPending()
    {
        // The receipt names the finding but only to record that its own request was NOT run.
        var receipt = new FindingEvidenceReceipt(
            "batched-receipt",
            CandidateSha,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "OtherTests")]),
            Accepted: true,
            Passed: true,
            "executed a different request in this batch",
            RequestDispositions:
            [
                new FindingEvidenceRequestDisposition(
                    "T-FINDING",
                    FindingEvidenceExecutionClassifier.BuildRequestIdentity(Request),
                    "pending-standalone",
                    "incompatible-project")
            ]);
        var task = TaskWithReceipts(receipt);

        Assert.Equal(
            FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Superseded_disposition_at_the_current_candidate_is_executed")]
    public void SupersededDispositionAtTheCurrentCandidateIsExecuted()
    {
        var receipt = new FindingEvidenceReceipt(
            "red-receipt",
            CandidateSha,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "OtherTests")]),
            Accepted: true,
            Passed: false,
            "actionable candidate red",
            RequestDispositions:
            [
                new FindingEvidenceRequestDisposition(
                    "T-FINDING",
                    FindingEvidenceExecutionClassifier.BuildRequestIdentity(Request),
                    "superseded",
                    "superseded-by-actionable-red")
            ]);
        var task = TaskWithReceipts(receipt);

        Assert.Equal(
            FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Legacy_receipt_without_dispositions_matches_on_request_identity")]
    public void LegacyReceiptWithoutDispositionsMatchesOnRequestIdentity()
    {
        var task = TaskWithReceipts(new FindingEvidenceReceipt(
            "legacy-receipt", CandidateSha, Request, Accepted: true, Passed: true, "legacy receipt"));

        Assert.Equal(
            FindingEvidenceExecutionState.ExecutedOnCandidate,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), CandidateSha));
    }

    [Xunit.Theory(DisplayName = "Permanent_refusals_stay_writable_and_transient_ones_stay_pending")]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.UnparseableSelection, FindingEvidenceExecutionState.PermanentlyRefused)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.UnsupportedProject, FindingEvidenceExecutionState.PermanentlyRefused)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.SupersededByActionableRed, FindingEvidenceExecutionState.PermanentlyRefused)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.Unknown, FindingEvidenceExecutionState.PermanentlyRefused)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.PerRoundCap, FindingEvidenceExecutionState.PermanentlyRefused)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.RunFailed, FindingEvidenceExecutionState.PendingExecution)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.ExecutorUnavailable, FindingEvidenceExecutionState.PendingExecution)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.CandidateShaMissing, FindingEvidenceExecutionState.PendingExecution)]
    [Xunit.InlineData(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, FindingEvidenceExecutionState.PendingExecution)]
    public void PermanentRefusalsStayWritableAndTransientOnesStayPending(
        FindingEvidenceNotHonouredReason reason,
        FindingEvidenceExecutionState expected)
    {
        var task = TaskWithReceipts();
        var finding = Finding(outcome: new FindingEvidenceOutcome(
            Honoured: false, ReceiptId: null, Reason: reason, Detail: "typed refusal"));

        Assert.Equal(expected, FindingEvidenceExecutionClassifier.Classify(task, finding, CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Every_not_honoured_reason_has_a_decision_table_row")]
    public void EveryNotHonouredReasonHasADecisionTableRow()
    {
        // The decision table above carries one row per reason, including the retired-but-persisted
        // PerRoundCap. A new reason must arrive with its own row rather than inherit the fail-safe
        // default silently, so this count is the reminder that the table is the contract.
        Assert.Equal(9, Enum.GetValues<FindingEvidenceNotHonouredReason>().Length);
    }

    [Xunit.Fact(DisplayName = "Legacy_receipt_with_a_null_request_covers_nothing_and_stays_pending")]
    public void LegacyReceiptWithANullRequestCoversNothingAndStaysPending()
    {
        // A persisted receipt whose non-nullable Request deserialized as null names no request at
        // all. Classification renders briefs, so it must read as unmeasured, never fault the caller.
        var task = TaskWithReceipts(new FindingEvidenceReceipt(
            "malformed-legacy-receipt", CandidateSha, null!, Accepted: true, Passed: true, "legacy receipt"));

        Assert.Equal(
            FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), CandidateSha));
    }

    [Xunit.Fact(DisplayName = "Outcome_without_a_reason_fails_safe_as_a_permanent_refusal")]
    public void OutcomeWithoutAReasonFailsSafeAsAPermanentRefusal()
    {
        var task = TaskWithReceipts();
        var finding = Finding(outcome: new FindingEvidenceOutcome(Honoured: false));

        Assert.Equal(
            FindingEvidenceExecutionState.PermanentlyRefused,
            FindingEvidenceExecutionClassifier.Classify(task, finding, CandidateSha));
    }

    [Xunit.Theory(DisplayName = "Unknown_candidate_is_conservatively_writable")]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("")]
    [Xunit.InlineData("   ")]
    [Xunit.InlineData(FindingEvidenceExecutionClassifier.UnavailableCandidateSha)]
    public void UnknownCandidateIsConservativelyWritable(string? candidateSha)
    {
        var task = TaskWithReceipts(Receipt("current-receipt", CandidateSha, executed: true));

        Assert.Equal(
            FindingEvidenceExecutionState.CandidateUnknown,
            FindingEvidenceExecutionClassifier.Classify(task, Finding(), candidateSha));
    }

    [Xunit.Fact(DisplayName = "Pending_only_requires_every_finding_to_be_awaiting_execution")]
    public void PendingOnlyRequiresEveryFindingToBeAwaitingExecution()
    {
        var task = TaskWithReceipts();
        var pending = Finding();
        var writable = FindingWithoutRequest();

        Assert.True(FindingEvidenceExecutionClassifier.IsPendingExecutionOnly([pending], task, CandidateSha));
        Assert.False(FindingEvidenceExecutionClassifier.IsPendingExecutionOnly(
            [pending, writable], task, CandidateSha));
        Assert.False(FindingEvidenceExecutionClassifier.IsPendingExecutionOnly([], task, CandidateSha));
        Assert.False(FindingEvidenceExecutionClassifier.IsPendingExecutionOnly([pending], task, null));
    }

    [Xunit.Fact(DisplayName = "Execution_state_wire_values_are_stable")]
    public void ExecutionStateWireValuesAreStable()
    {
        Assert.Equal("none-requested", FindingEvidenceExecutionClassifier.ToWireValue(FindingEvidenceExecutionState.NoneRequested));
        Assert.Equal("pending-execution", FindingEvidenceExecutionClassifier.ToWireValue(FindingEvidenceExecutionState.PendingExecution));
        Assert.Equal("executed-on-candidate", FindingEvidenceExecutionClassifier.ToWireValue(FindingEvidenceExecutionState.ExecutedOnCandidate));
        Assert.Equal("permanently-refused", FindingEvidenceExecutionClassifier.ToWireValue(FindingEvidenceExecutionState.PermanentlyRefused));
        Assert.Equal("candidate-unknown", FindingEvidenceExecutionClassifier.ToWireValue(FindingEvidenceExecutionState.CandidateUnknown));
    }

    [Xunit.Fact]
    public void EvidenceDeliveryRetryCountIsScopedToTaskCandidateAndFinding()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var otherTaskId = TaskId.New();
        var timeline = new[]
        {
            RetryEvent(goalId, taskId, CandidateSha, "T-FINDING"),
            RetryEvent(goalId, taskId, CandidateSha, "T-FINDING"),
            RetryEvent(goalId, taskId, OldCandidateSha, "T-FINDING"),
            RetryEvent(goalId, taskId, CandidateSha, "OTHER"),
            RetryEvent(goalId, otherTaskId, CandidateSha, "T-FINDING")
        };

        Assert.Equal(
            2,
            FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                timeline, taskId, CandidateSha, "T-FINDING"));
    }

    private static ProgressEvent RetryEvent(
        GoalId goalId,
        TaskId taskId,
        string candidateSha,
        string findingId) =>
        new(
            goalId,
            taskId,
            ProgressKind.TaskRetried,
            $"finding evidence-on-demand: candidate_sha={candidateSha}; finding_ids={findingId}; receipt_ids=receipt; retry",
            DateTimeOffset.UtcNow);

    private static ReviewFinding Finding(
        string id = "T-FINDING",
        FindingEvidenceOutcome? outcome = null) =>
        new(
            id,
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Test.cs", "Test.Run"),
            "The candidate requires a finding-specific response.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness,
            Request,
            outcome);

    private static ReviewFinding FindingWithoutRequest(string id = "T-WRITABLE") =>
        Finding(id) with { EvidenceRequest = null };

    private static FindingEvidenceReceipt Receipt(
        string receiptId,
        string candidateSha,
        bool executed,
        bool passed = true) =>
        new(
            receiptId,
            candidateSha,
            Request,
            Accepted: true,
            Passed: passed,
            $"focused evidence receipt {receiptId}",
            RequestDispositions:
            [
                new FindingEvidenceRequestDisposition(
                    "T-FINDING",
                    FindingEvidenceExecutionClassifier.BuildRequestIdentity(Request),
                    executed ? "executed-standalone" : "pending-standalone",
                    "only-request")
            ]);

    private static TaskSpec TaskWithReceipts(params FindingEvidenceReceipt[] receipts)
    {
        var task = new TaskSpec(TaskId.New(), "Verify the candidate", AgentRole.Tester);
        task.RecordVerification(new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            DateTimeOffset.UtcNow,
            FindingEvidenceReceipts: receipts.Length == 0 ? null : receipts));
        return task;
    }
}
