namespace Mcg.AgentOrchestrator.Core;

public sealed record CriterionOwnershipDerivationResult(
    IReadOnlyList<string> AcceptanceGateOwned,
    IReadOnlyList<string> OperatorOwned)
{
    public IReadOnlyList<CriterionOwnershipDerivationIssue> Issues { get; init; } = [];
}

public sealed record CriterionOwnershipDerivationIssue(int CriterionIndex, string Diagnostic);

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
        var issues = new List<CriterionOwnershipDerivationIssue>();

        for (var index = 0; index < newCriteria.Count; index++)
        {
            var criterion = newCriteria[index];
            if (!seen.Add(criterion))
                continue;

            var marker = AcceptanceCriterionOwnershipMarker.Classify(criterion);
            if (AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(criterion))
            {
                issues.Add(new(index,
                    $"Declared criterion {index + 1} claims both Operator and Acceptance evidence ownership: '{criterion}'."));
                continue;
            }

            var declared = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion);
            var operatorOwnedByMarker = marker.Classification ==
                AcceptanceCriterionOwnershipClassification.OperatorOwned;
            var gateOwnedByMarker = !operatorOwnedByMarker &&
                AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion);
            if ((priorOperator.Contains(criterion) && declared.Kind == DeclaredEvidenceOwnerKind.None) ||
                operatorOwnedByMarker)
            {
                operatorOwned.Add(criterion);
            }
            else if ((priorGate.Contains(criterion) && declared.Kind == DeclaredEvidenceOwnerKind.None) || gateOwnedByMarker ||
                     declared.Kind == DeclaredEvidenceOwnerKind.AcceptanceGate)
            {
                gateOwned.Add(criterion);
            }
            else if (marker.Classification == AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal)
            {
                issues.Add(new(index,
                    $"Declared criterion {index + 1} is REAL-WORLD-DEPENDENT without an authoritative evidence owner: '{criterion}'."));
            }
        }

        return new CriterionOwnershipDerivationResult(gateOwned, operatorOwned) { Issues = issues };
    }
}
