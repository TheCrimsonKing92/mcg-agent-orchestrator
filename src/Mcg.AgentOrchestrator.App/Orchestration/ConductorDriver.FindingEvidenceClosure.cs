using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private FailedGoalFindingObservation BuildReceiptClosureOrDeliveryRetry(
        Goal goal,
        TaskSpec task,
        string candidateSha,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> receiptIds,
        string summary)
    {
        if (task.RequiredRole == AgentRole.Tester &&
            (_cohortKernel ?? _conductorTickKernel) is { } kernel)
        {
            var currentTask = kernel.GetTask(goal.Id, task.Id);
            if (FindingEvidenceReceiptSelector.TrySelectReceiptClosableFindings(goal, currentTask, candidateSha, out var closable) &&
                !kernel.HumanInputRequests.Any(request =>
                    request.GoalId == goal.Id && request.TaskId == task.Id &&
                    !request.IsCompleted && HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind)))
            {
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
                var note = $"FINDING_RECEIPT_CLOSURE task={task.Id.Value[..8]} " +
                    $"findings={string.Join(',', resolved.Keys.Order(StringComparer.Ordinal))} " +
                    $"receipts={string.Join(',', closable.Select(pair => pair.Receipt.ReceiptId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))} " +
                    $"candidate={reviewedSha}; no worker dispatched";
                kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                    "conductor finding-receipt-closure", prior.WorkingDirectory, 0, note, "", _utcNow(),
                    ReviewedCommit: reviewedSha,
                    MergedReviewFindings: prior.MergedReviewFindings!
                        .Select(finding => resolved.GetValueOrDefault(finding.StableId, finding)).ToArray(),
                    FindingEvidenceReceipts: prior.FindingEvidenceReceipts));
                if (currentTask.Status != WorkTaskStatus.Completed)
                    throw new InvalidOperationException($"Receipt closure did not complete Tester task {task.Id}.");
                kernel.RecordTaskNote(goal.Id, task.Id, note);
                return FailedGoalFindingObservation.Observed(
                    FailedGoalFindingObservationKind.FindingEvidencePending, note);
            }
        }

        var receiptTask = (_cohortKernel ?? _conductorTickKernel)?.GetTask(goal.Id, task.Id) ?? task;
        var openFindings = ReviewFindings.GetOpenBlockingFindings(
            receiptTask.LastVerification?.MergedReviewFindings ?? findings,
            goal.EffectiveAcceptanceCriteriaCorrections);
        if (task.RequiredRole == AgentRole.Tester && openFindings.Count > 0 &&
            FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(receiptTask, candidateSha, receiptIds))
        {
            return BuildPassingEvidenceOpenFindingDecision(
                goal, task, candidateSha, receiptIds, openFindings);
        }

        return BuildCappedFindingEvidenceDeliveryRetry(goal, task, candidateSha, findings, receiptIds, summary);
    }
}
