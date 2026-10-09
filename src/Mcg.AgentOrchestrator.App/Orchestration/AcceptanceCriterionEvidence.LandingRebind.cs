using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class AcceptanceCriterionEvidence
{
    private static string? TryRebindPendingAcceptanceObligations(
        Goal goal, string candidateSha, AgentOrchestratorKernel kernel, string integrationBranch, string? executionDirectory)
    {
        var pending = goal.OutstandingCriterionEvidenceObligations.Where(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                item.State == CriterionEvidenceState.Pending &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .ToArray();
        var boundElsewhere = pending.Where(item =>
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var evidenceByHead = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Validate every old binding before changing any obligation.
        foreach (var obligation in boundElsewhere)
        {
            var oldHead = obligation.ExpectedCandidateSha!;
            if (evidenceByHead.ContainsKey(oldHead)) continue;
            if (!GoalWorktrees.TryComputePatchEquivalence(
                    executionDirectory ?? string.Empty, oldHead, candidateSha, integrationBranch,
                    out var evidence, out var reason))
            {
                return FormatRefusedCarryDiagnostic(
                    candidateSha, obligation, $"reason={reason}; head={oldHead}");
            }
            evidenceByHead.Add(oldHead, evidence);
        }

        foreach (var obligation in pending)
        {
            if (string.Equals(obligation.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
                continue;
            kernel.MapCriterionEvidenceOwner(
                goal.Id,
                obligation.CriterionIndex,
                obligation.CriterionVersion,
                CriterionEvidenceOwner.Acceptance,
                string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha)
                    ? "conductor deterministic full acceptance"
                    : "conductor patch-equivalent landing carry",
                obligation.RequiredScope,
                obligation.FindingStableId,
                candidateSha);
        }

        if (boundElsewhere.Length > 0)
        {
            GoalOperationJournal.Completed(
                executionDirectory,
                goal,
                "conductor:criterion-evidence-landing-carry",
                $"obligations={string.Join(',', boundElsewhere.Select(item => item.Id))}; " +
                $"oldHeads={string.Join(',', evidenceByHead.Keys)}; newHead={candidateSha}; " +
                $"equivalence={string.Join(" | ", evidenceByHead.Select(item => $"{item.Key}: {item.Value}"))}");
        }
        return null;
    }
}
