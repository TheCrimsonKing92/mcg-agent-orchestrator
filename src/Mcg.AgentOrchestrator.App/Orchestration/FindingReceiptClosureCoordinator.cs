using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the all-or-nothing Tester closure and preservation of its evidence lineage.
internal static class FindingReceiptClosureCoordinator
{
    internal static bool TryClose(AgentOrchestratorKernel kernel, Goal goal, TaskId taskId,
        string candidateSha, Func<DateTimeOffset> utcNow, out FindingReceiptClosureDiagnosis diagnosis,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        var currentTask = kernel.GetTask(goal.Id, taskId);
        diagnosis = FindingReceiptClosureDiagnosis.Evaluate(goal, currentTask, candidateSha,
            kernel.HumanInputRequests.Any(request =>
                request.GoalId == goal.Id && request.TaskId == taskId &&
                !request.IsCompleted && HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind)));
        if (!diagnosis.IsClosable) return false;

        var closable = diagnosis.ClosableFindings;
        var prior = currentTask.LastVerification!;
        var reviewedSha = prior.ReviewedCommit!.Trim();
        var resolved = closable.ToDictionary(pair => pair.Finding.StableId, pair =>
            pair.Finding with
            {
                State = ReviewFindingState.Resolved,
                Description = $"Closed by conductor receipt closure: receipt {pair.Receipt.ReceiptId}; " +
                    $"request {pair.Finding.EvidenceOutcome!.RequestedSelectionIdentity ?? BuildFindingEvidenceIdentity(pair.Receipt.Request)}; " +
                    $"candidate {reviewedSha}."
            }, StringComparer.Ordinal);
        var note = $"FINDING_RECEIPT_CLOSURE task={taskId.Value[..8]} " +
            $"findings={string.Join(',', resolved.Keys.Order(StringComparer.Ordinal))} " +
            $"receipts={string.Join(',', closable.Select(pair => pair.Receipt.ReceiptId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))} " +
            $"candidate={reviewedSha}{OutsideDiffFindingRerun.FormatClosureDecision(closable)}; no worker dispatched";
        kernel.RecordTaskVerification(goal.Id, taskId, new TaskVerificationRecord(
            "conductor finding-receipt-closure", prior.WorkingDirectory, 0, OutsideDiffFindingRerun.ClosureOutput(note, resolved.Values), "", utcNow(),
            ReviewedCommit: reviewedSha,
            CandidateIdentity: prior.CandidateIdentity,
            DispatchStartedAt: prior.DispatchStartedAt,
            MergedReviewFindings: prior.MergedReviewFindings!
                .Select(finding => resolved.GetValueOrDefault(finding.StableId, finding)).ToArray(),
            FindingEvidenceReceipts: prior.FindingEvidenceReceipts,
            FindingEvidenceRequestDispositions: prior.FindingEvidenceRequestDispositions));
        if (currentTask.Status != WorkTaskStatus.Completed)
            throw new InvalidOperationException($"Receipt closure did not complete Tester task {taskId}.");
        kernel.RecordTaskNote(goal.Id, taskId, note);
        observation = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingEvidencePending, note);
        return true;
    }
}
