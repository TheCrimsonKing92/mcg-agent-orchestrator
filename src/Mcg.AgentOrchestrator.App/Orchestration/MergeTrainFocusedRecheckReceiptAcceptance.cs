using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// This additional contract applies only to composite receipts. Ordinary gate
// receipts retain their existing authoritative-evidence predicate.
internal static class MergeTrainFocusedRecheckReceiptAcceptance
{
    internal static bool IsComposite(MergeTrainReceipt receipt) => receipt.Recheck is not null ||
        receipt.ReceiptId.StartsWith(MergeTrainFocusedRecheckReceipt.Prefix, StringComparison.Ordinal);

    internal static bool Accepts(MergeTrainReceipt receipt, MergeTrainAcceptanceStore store,
        bool requireContentBound = true)
    {
        try { return Validate(receipt, store, requireContentBound); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException) { return false; }
    }

    private static bool Validate(MergeTrainReceipt receipt, MergeTrainAcceptanceStore store, bool requireContentBound)
    {
        var proof = receipt.Recheck;
        if (proof is null || receipt.Outcome != MergeTrainGateOutcome.Passed || !receipt.ValidForLanding ||
            receipt.GateExitCode != 0 || receipt.FailedChecks.Count != 0 ||
            !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(receipt.GateTestResultPaths) ||
            (requireContentBound && !receipt.HasAuthoritativeLandingEvidence) ||
            string.IsNullOrWhiteSpace(proof.RedTrainId) || string.IsNullOrWhiteSpace(proof.RedReceiptId) ||
            string.IsNullOrWhiteSpace(proof.TrainAttemptId) || string.IsNullOrWhiteSpace(proof.RecheckAttemptId) ||
            receipt.GateAttemptId != proof.RecheckAttemptId ||
            proof.AttributionSource is not ("ownership" or "impact" or "reach" or "path" or "reproduction") ||
            !proof.DependencySelectionResolved || proof.LandingPathsDisjoint != true ||
            proof.NotRunSelectionCount != 0 || proof.Selection.Count == 0 ||
            proof.RecheckMainRevision != receipt.Identity.ObservedMainRevision ||
            proof.RecheckTreeRevision != receipt.Identity.TrainTreeRevision ||
            receipt.ReceiptId != MergeTrainFocusedRecheckReceipt.ReceiptId(receipt.Identity, proof)) return false;

        var red = store.TryReadReceipt(proof.RedTrainId);
        if (red is null || red.ReceiptId != proof.RedReceiptId || red.Outcome != MergeTrainGateOutcome.Failed ||
            red.GateAttemptId != proof.TrainAttemptId ||
            red.Recheck is not null ||
            red.FailedChecks.Contains(SourceSizeRatchetPreflight.CheckName, StringComparer.Ordinal) ||
            !AcceptanceCohortGateEvidence.HasContentBoundEvidence(red.GateTestResultPaths, red.GateEvidenceArtifacts) ||
            !MergeTrainFocusedRecheckDecision.MatchesRemaining(red, proof.CulpritGoalId, receipt.Identity) ||
            !MergeTrainFocusedRecheckDecision.TryReadFailingIdentities(red, out var failing) ||
            !MergeTrainGateReceiptExecution.HasExplainedFailedChecks(red, failing)) return false;
        var culprit = red.Identity.Members.SingleOrDefault(member => member.GoalId == proof.CulpritGoalId);
        if (culprit is null || !MergeTrainFocusedRecheckDecision.PathsAreDisjoint(red.Identity.Members, culprit)) return false;

        var expected = MergeTrainFocusedRecheckDecision.Select(failing, proof.DependencyClasses);
        if (expected is null || !expected.Identities.SequenceEqual(proof.Selection, StringComparer.Ordinal) ||
            !proof.ExecutedSelectors.SequenceEqual(proof.Selection, StringComparer.Ordinal)) return false;
        var execution = MergeTrainReproductionAttribution.ReadResults(receipt.GateTestResultPaths);
        return execution is not null && execution.Failures.Count == 0 &&
            proof.Selection.All(selector => execution.ExecutedTests.Any(test => MergeTrainFocusedRecheckDecision.Covers(selector, test)));
    }
}
