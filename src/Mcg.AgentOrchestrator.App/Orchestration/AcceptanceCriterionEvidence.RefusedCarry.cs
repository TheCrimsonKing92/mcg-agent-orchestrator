using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class AcceptanceCriterionEvidence
{
    internal static ConductorAdvanceOutcome.Held? DescribeRefusedCarryHold(
        Goal goal, string candidateSha, string integrationBranch, string? executionDirectory)
    {
        if (TryPlanPatchEquivalentBindings(goal, candidateSha, integrationBranch, executionDirectory,
                out _, out _, out _, out var refusal))
            return null;
        var obligation = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .FirstOrDefault(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal));
        return obligation is null ? null : new ConductorAdvanceOutcome.Held(
            GoalLifecycleState.Verified,
            FormatRefusedCarryHold(goal, candidateSha, obligation, refusal),
            StableIdentity: HoldIdentity(goal, candidateSha));
    }

    private static string FormatRefusedCarryHold(
        Goal goal, string candidateSha, CriterionEvidenceObligation obligation, string? refusal)
    {
        var prefix = FormatRefusedCarryDiagnostic(candidateSha, obligation, refusal);
        var commands = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .Where(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal))
            .Select(item => $"criterion-evidence-map --goal {goal.Id.Value} --criterion {item.CriterionNumber} --version {item.CriterionVersion} acceptance {item.RequiredScope} {item.FindingStableId ?? item.Id} {candidateSha}");
        return $"{prefix} Operator rebind: {string.Join("; ", commands)}";
    }

    private static string FormatRefusedCarryDiagnostic(
        string candidateSha, CriterionEvidenceObligation obligation, string? refusal) =>
        $"Acceptance passed for {candidateSha}, but obligation '{obligation.Id}' is bound to {obligation.ExpectedCandidateSha ?? "no candidate"}. Rebind the obligation to the current candidate before recording its evidence. Carry-forward refused: {refusal ?? "reason=not-eligible"}.";

    private static bool TryPlanPatchEquivalentBindings(
        Goal goal, string candidateSha, string integrationBranch, string? executionDirectory,
        out CriterionEvidenceObligation[] eligible,
        out string[] oldHeads,
        out Dictionary<string, string> evidenceByHead,
        out string? refusal)
    {
        refusal = null;
        eligible = goal.GetOutstandingCriterionEvidenceObligations(candidateSha)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ExpectedCandidateSha) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                (item.Owner == CriterionEvidenceOwner.Acceptance &&
                 (item.State == CriterionEvidenceState.Pending ||
                  item.State == CriterionEvidenceState.Satisfied && !string.IsNullOrWhiteSpace(executionDirectory)) &&
                 string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal) ||
                 item.Owner == CriterionEvidenceOwner.Operator &&
                 item.State == CriterionEvidenceState.Satisfied))
            .ToArray();
        var eligibleBindings = eligible;
        oldHeads = eligible
            .OrderBy(item => item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Satisfied)
            .Select(item => item.ExpectedCandidateSha!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        evidenceByHead = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (eligible.Length == 0) return false;
        if (eligible.Any(item => item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Satisfied) &&
            goal.GetOutstandingCriterionEvidenceObligations(candidateSha).Any(item =>
                item.Owner == CriterionEvidenceOwner.Acceptance &&
                string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal) &&
                !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                !eligibleBindings.Contains(item)))
            return false;
        if (string.IsNullOrWhiteSpace(executionDirectory))
        {
            refusal = "reason=no-execution-directory";
            return false;
        }

        foreach (var oldHead in oldHeads)
        {
            if (!GoalWorktrees.TryComputePatchEquivalence(
                    executionDirectory, oldHead, candidateSha, integrationBranch, out var evidence, out var reason))
            {
                refusal = eligible.Any(item => item.Owner == CriterionEvidenceOwner.Acceptance &&
                    item.State == CriterionEvidenceState.Satisfied &&
                    string.Equals(item.ExpectedCandidateSha, oldHead, StringComparison.OrdinalIgnoreCase))
                    && !eligible.Any(item =>
                        (item.Owner == CriterionEvidenceOwner.Operator || item.State == CriterionEvidenceState.Pending) &&
                        string.Equals(item.ExpectedCandidateSha, oldHead, StringComparison.OrdinalIgnoreCase))
                        ? null
                        : $"reason={reason}; head={oldHead}";
                return false;
            }
            evidenceByHead.Add(oldHead, evidence);
        }
        return true;
    }
}
