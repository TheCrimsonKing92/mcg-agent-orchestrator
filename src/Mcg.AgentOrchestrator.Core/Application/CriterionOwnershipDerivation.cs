namespace Mcg.AgentOrchestrator.Core;

public sealed record CriterionOwnershipDerivationResult(
    IReadOnlyList<string> AcceptanceGateOwned,
    IReadOnlyList<string> OperatorOwned);

public static class CriterionOwnershipDerivation
{
    public static CriterionOwnershipDerivationResult DeriveForRevision(
        IReadOnlyList<string> newCriteria,
        IReadOnlyList<string> priorGateOwned,
        IReadOnlyList<string> priorOperatorOwned)
    {
        var priorGate = priorGateOwned.ToHashSet(StringComparer.Ordinal);
        var priorOperator = priorOperatorOwned.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gateOwned = new List<string>();
        var operatorOwned = new List<string>();

        foreach (var item in newCriteria)
        {
            var criterion = item;
            if (!seen.Add(criterion))
                continue;

            var marker = AcceptanceCriterionOwnershipMarker.Classify(criterion);
            var declared = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);
            var operatorOwnedByMarker = marker.Classification is
                AcceptanceCriterionOwnershipClassification.OperatorOwned or
                AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal;
            var gateOwnedByMarker = !operatorOwnedByMarker &&
                AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion);
            if ((priorOperator.Contains(criterion) && declared.Kind == DeclaredEvidenceOwnerKind.None) ||
                operatorOwnedByMarker)
            {
                operatorOwned.Add(criterion);
            }
            else if (priorGate.Contains(criterion) || gateOwnedByMarker ||
                     declared.Kind == DeclaredEvidenceOwnerKind.AcceptanceGate)
            {
                gateOwned.Add(criterion);
            }
        }

        return new CriterionOwnershipDerivationResult(gateOwned, operatorOwned);
    }
}
