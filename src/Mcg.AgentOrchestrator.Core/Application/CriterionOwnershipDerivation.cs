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
        var priorGate = priorGateOwned.Select(item => item.Trim()).ToHashSet(StringComparer.Ordinal);
        var priorOperator = priorOperatorOwned.Select(item => item.Trim()).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gateOwned = new List<string>();
        var operatorOwned = new List<string>();

        foreach (var item in newCriteria)
        {
            var criterion = item.Trim();
            if (!seen.Add(criterion))
                continue;

            if (priorOperator.Contains(criterion) ||
                AcceptanceCriterionOwnershipMarker.HasOperatorOwnershipPhrase(criterion))
            {
                operatorOwned.Add(criterion);
            }
            else if (priorGate.Contains(criterion) ||
                     AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion))
            {
                gateOwned.Add(criterion);
            }
        }

        return new CriterionOwnershipDerivationResult(gateOwned, operatorOwned);
    }
}
