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
        var diagnosis = FindingReceiptClosureDiagnosis.Evaluate(goal, task, candidateSha);
        closable = diagnosis.ClosableFindings;
        return diagnosis.IsClosable;
    }
}
