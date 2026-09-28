using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class GoalRefinementService
{
    private static Dictionary<int, DeclaredEvidenceOwner> ClaimDeclaredEvidenceOwners(
        IReadOnlyList<string> acceptanceCriteria,
        List<string> operatorOwnedCriteria,
        List<string> acceptanceGateOwnedCriteria,
        Dictionary<int, string> claimedOwners)
    {
        var declared = new Dictionary<int, DeclaredEvidenceOwner>();
        for (var index = 0; index < acceptanceCriteria.Count; index++)
        {
            var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(acceptanceCriteria[index]);
            if (owner.Kind == DeclaredEvidenceOwnerKind.None)
                continue;

            declared.Add(index, owner);
            claimedOwners[index] = owner.WireToken;
            if (owner.Kind == DeclaredEvidenceOwnerKind.AcceptanceGate &&
                !acceptanceGateOwnedCriteria.Contains(acceptanceCriteria[index], StringComparer.Ordinal))
            {
                acceptanceGateOwnedCriteria.Add(acceptanceCriteria[index]);
            }
        }

        operatorOwnedCriteria.RemoveAll(criterion =>
            declared.ContainsKey(FindCriterionIndex(acceptanceCriteria, criterion)));
        return declared;
    }

    private static bool TryApplyDeclaredOwnerOverride(
        int criterionIndex,
        string requestedOwner,
        IReadOnlyDictionary<int, DeclaredEvidenceOwner> declared,
        HashSet<int> overriddenIndexes,
        List<string> diagnostics)
    {
        if (!declared.TryGetValue(criterionIndex, out var owner))
            return false;

        if ((requestedOwner == "operator" ||
             (requestedOwner == "acceptance-gate" && owner.Kind == DeclaredEvidenceOwnerKind.Worker)) &&
            overriddenIndexes.Add(criterionIndex))
        {
            diagnostics.Add(
                $"Spec refiner owner overridden: declared criterion {criterionIndex + 1} declares {owner.WireToken}; refiner claimed {requestedOwner}");
        }

        return true;
    }
}
