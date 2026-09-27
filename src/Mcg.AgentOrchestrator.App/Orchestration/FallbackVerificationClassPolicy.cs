using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FallbackVerificationClassPolicy
{
    public static VerificationClass Classify(IReadOnlyList<string> criteria) =>
        criteria.Count > 0 && criteria.All(IsClassifiedTestVerifiable)
            ? VerificationClass.TestVerifiable
            : VerificationClass.RealWorldDependent;

    public static string BuildOwnershipReceipt(RefinedSpec spec)
    {
        var gateOwned = spec.AcceptanceGateOwnedAcceptanceCriteria.ToHashSet(StringComparer.Ordinal);
        var operatorOwned = spec.OperatorOwnedAcceptanceCriteria.ToHashSet(StringComparer.Ordinal);
        var needingOwnership = spec.AcceptanceCriteria
            .Select((criterion, index) => (criterion, index))
            .Where(item => !gateOwned.Contains(item.criterion) && !operatorOwned.Contains(item.criterion))
            .Select(item => $"{item.index + 1}:{JsonSerializer.Serialize(item.criterion)}")
            .ToArray();

        return $"spec_refinement_fallback_ownership fallback=true verification_class={spec.VerificationClass} " +
            $"needs_ownership={(needingOwnership.Length == 0 ? "none" : string.Join(",", needingOwnership))}";
    }

    private static bool IsClassifiedTestVerifiable(string criterion)
    {
        var classification = AcceptanceCriterionOwnershipMarker.Classify(criterion).Classification;
        if (classification is AcceptanceCriterionOwnershipClassification.OperatorOwned or
            AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal)
            return false;

        return AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion) ||
            AcceptanceCriterionOwnershipMarker.ExtractTrailingRegion(criterion)
                .Contains("TEST-VERIFIABLE", StringComparison.OrdinalIgnoreCase);
    }
}
