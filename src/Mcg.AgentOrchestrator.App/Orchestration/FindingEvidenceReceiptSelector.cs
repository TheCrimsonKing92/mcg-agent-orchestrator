using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Selects receipts from task evidence; verification mutation and dispatch stay with the conductor.
internal static class FindingEvidenceReceiptSelector
{
    internal static bool SuppliedReceiptsPassAtCandidate(
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

    internal static bool TrySelectReceiptClosableFindings(
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
