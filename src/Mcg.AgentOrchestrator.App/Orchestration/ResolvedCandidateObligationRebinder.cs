using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ResolvedCandidateObligationRebinder
{
    internal static IReadOnlyList<CriterionEvidenceObligation> Rebind(
        AgentOrchestratorKernel kernel, Goal goal, string candidateSha)
    {
        var pending = goal.OutstandingCriterionEvidenceObligations.Where(item =>
            item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Pending &&
            string.Equals(item.RequiredScope, CriterionEvidenceScopes.FullAcceptanceGate, StringComparison.Ordinal) &&
            !string.Equals(item.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase)).ToArray();
        return pending.Select(item => kernel.MapCriterionEvidenceOwner(goal.Id, item.CriterionIndex, item.CriterionVersion,
                CriterionEvidenceOwner.Acceptance, "conductor ratchet conflict resolution", item.RequiredScope,
                item.FindingStableId, candidateSha)).ToArray();
    }
}
