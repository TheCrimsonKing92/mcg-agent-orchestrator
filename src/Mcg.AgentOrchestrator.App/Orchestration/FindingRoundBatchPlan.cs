using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Round-local scheduling facts, recomputed from durable receipts and completed-run events.
internal sealed record FindingRoundBatchPlan(
    IReadOnlyList<FindingEvidenceBatch> Passed,
    IReadOnlyList<(FindingEvidenceBatch Batch, FindingEvidenceReceipt Receipt)> RedCovered,
    IReadOnlyList<FindingEvidenceBatch> Executable,
    (FindingEvidenceBatch Batch, int Count, string ReceiptId)? Capped)
{
    internal ReviewFinding[] CoveredFindings => RedCovered.SelectMany(pair => pair.Batch.Findings)
        .DistinctBy(finding => finding.StableId).ToArray();

    internal string[] CoveredReceiptIds => RedCovered.Select(pair => pair.Receipt.ReceiptId)
        .Distinct(StringComparer.Ordinal).ToArray();

    internal string CoveredEvidence => string.Join("; ", RedCovered.Select(pair =>
        $"request_identity={pair.Batch.Identity}; receipt_id={pair.Receipt.ReceiptId}"));
}
