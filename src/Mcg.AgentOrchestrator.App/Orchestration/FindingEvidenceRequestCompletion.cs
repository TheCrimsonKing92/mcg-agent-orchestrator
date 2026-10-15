using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Completed request decisions require candidate execution covering the member's remaining selections.
internal static class FindingEvidenceRequestCompletion
{
    internal static IReadOnlyList<FindingEvidenceRequestDisposition> BuildDispositions(
        IReadOnlyList<FindingEvidenceRequestDisposition> initialRequestDispositions,
        FindingEvidenceBatch batch,
        FindingEvidenceReceipt receipt,
        Func<FindingEvidenceSelection, string> formatSelection,
        bool actionableCandidateRed = false)
    {
        var executedCoverage = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, formatSelection)
            .ToHashSet();
        var candidateExecuted = (receipt.Arms ?? []).Any(arm =>
            arm is { Arm: FindingEvidenceArm.Candidate, Accepted: true, ExecutedTestCount: > 0 });
        return initialRequestDispositions.Select(disposition =>
        {
            if (!disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal)) return disposition;
            var member = batch.Members.Single(member => member.Identity == disposition.RequestIdentity);
            // The member contains only its uncovered remainder after prior receipt reuse.
            // Acceptance and empty omission metadata do not prove execution: a zero-test
            // apparatus result still has a receipt. Require candidate execution as well.
            var completed = candidateExecuted && executedCoverage.IsSupersetOf(
                FindingEvidenceRoundPlanner.ExpandSelections(member.TypedRequest.Selections));
            return completed
                ? disposition with
                {
                    Decision = disposition.CoveringRequestIdentity is null
                        ? FindingEvidenceRequestDecision.Executed : FindingEvidenceRequestDecision.CoveredByReceipt,
                    Reason = disposition.CoveringRequestIdentity is null ? disposition.Reason : "covered-by-executed-superset",
                    SourceReceiptIds = (disposition.SourceReceiptIds ?? []).Append(receipt.ReceiptId)
                        .Distinct(StringComparer.Ordinal).ToArray()
                }
                : disposition with
                {
                    Disposition = "incomplete",
                    Decision = actionableCandidateRed
                        ? FindingEvidenceRequestDecision.DeferredToNextCandidate
                        : FindingEvidenceRequestDecision.PendingExecution,
                    Reason = "candidate-selections-not-executed",
                    CoveringRequestIdentity = null,
                    SourceReceiptIds = (disposition.SourceReceiptIds ?? []).Append(receipt.ReceiptId)
                        .Distinct(StringComparer.Ordinal).ToArray()
                };
        }).ToArray();
    }
}
