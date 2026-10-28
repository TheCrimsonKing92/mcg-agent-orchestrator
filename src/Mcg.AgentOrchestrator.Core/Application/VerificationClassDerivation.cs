namespace Mcg.AgentOrchestrator.Core;

public static class VerificationClassDerivation
{
    public static VerificationClass Classify(IReadOnlyList<string> criteria) =>
        criteria.Count > 0 && criteria.All(IsClassifiedTestVerifiable)
            ? VerificationClass.TestVerifiable
            : VerificationClass.RealWorldDependent;

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
