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
            if (TrySelectReceiptClosableFindings(goal, currentTask, candidateSha, out var closable) &&
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
            SuppliedReceiptsPassAtCandidate(receiptTask, candidateSha, receiptIds))
        {
            return BuildPassingEvidenceOpenFindingDecision(
                goal, task, candidateSha, receiptIds, openFindings);
        }

        return BuildCappedFindingEvidenceDeliveryRetry(goal, task, candidateSha, findings, receiptIds, summary);
    }

    private static bool SuppliedReceiptsPassAtCandidate(
        TaskSpec task, string candidateSha, IReadOnlyList<string> receiptIds)
    {
        if (receiptIds.Count == 0 || !ConductorGitRevisionReader.IsValid(candidateSha)) return false;
        var receipts = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).ToArray();
        return receiptIds.All(id =>
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var receipt = receipts.LastOrDefault(candidate =>
                string.Equals(candidate.ReceiptId, id, StringComparison.Ordinal));
            return receipt is { Accepted: true, Passed: true } &&
                ConductorGitRevisionReader.IsValid(receipt.CandidateSha) &&
                string.Equals(receipt.CandidateSha.Trim(), candidateSha.Trim(), StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool TrySelectReceiptClosableFindings(
        Goal goal,
        TaskSpec task,
        string candidateSha,
        out IReadOnlyList<(ReviewFinding Finding, FindingEvidenceReceipt Receipt)> closable)
    {
        closable = [];
        if (task.RequiredRole != AgentRole.Tester || task.Status == WorkTaskStatus.Completed ||
            task.LastProcess is { IsRunning: true } ||
            task.LastVerification is not { WorkerResultPresent: true, ReviewFindingContractViolation: null } verification ||
            !verification.Succeeded ||
            !string.IsNullOrWhiteSpace(verification.HumanInputQuestion) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None ||
            WorkerResultBlockers.TryFindFailingTests(verification, out _) ||
            (WorkerResultBlockers.TryGetTestsStatus(verification, out var tests) &&
                tests == WorkerResultBlockers.TestsStatus.Inconclusive) ||
            !ConductorGitRevisionReader.IsValid(verification.ReviewedCommit) ||
            !ConductorGitRevisionReader.IsValid(candidateSha) ||
            !string.Equals(verification.ReviewedCommit!.Trim(), candidateSha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var open = ReviewFindings.GetOpenBlockingFindings(
            verification.MergedReviewFindings ?? [], goal.EffectiveAcceptanceCriteriaCorrections);
        if (open.Count == 0) return false;
        var selected = new List<(ReviewFinding, FindingEvidenceReceipt)>();
        foreach (var finding in open)
        {
            if (finding.Category != FindingCategory.TestEvidence ||
                finding.EvidenceOutcome is not
                {
                    Honoured: true, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
                    ReceiptId: { Length: > 0 } receiptId
                }) return false;

            var receipt = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                .LastOrDefault(candidate => string.Equals(candidate.ReceiptId, receiptId, StringComparison.Ordinal));
            if (receipt is not { Accepted: true, Passed: true } ||
                !ConductorGitRevisionReader.IsValid(receipt.CandidateSha) ||
                !string.Equals(receipt.CandidateSha.Trim(), verification.ReviewedCommit.Trim(), StringComparison.OrdinalIgnoreCase) ||
                receipt.Arms is not { } arms ||
                !arms.Any(arm => arm.Arm == FindingEvidenceArm.Candidate) ||
                arms.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).Any(arm =>
                    arm.Disposition != FindingEvidenceArmDisposition.Green || !arm.Accepted || !arm.Passed ||
                    !string.Equals(arm.Sha?.Trim(), verification.ReviewedCommit.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            selected.Add((finding, receipt));
        }
        closable = selected;
        return true;
    }
}
