using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;
using FocusedEvidenceIdentityExtraction = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceIdentityExtraction;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceSelectionCoverageInspector
{
    internal static FocusedEvidenceSelectionCoverage Inspect(
        IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> selections,
        IEnumerable<string>? trxPaths)
    {
        if (selections.Count == 0)
            return FocusedEvidenceSelectionCoverage.Empty;

        var extraction = GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities(trxPaths);
        var uncovered = selections
            .SelectMany(selection => selection.Where(token =>
                token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method))
            .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
            .Where(token => !extraction.Identities.Any(identity =>
                GoalAcceptanceVerifierTestTelemetry.IsFocusedEvidenceIdentityMatch(identity, token.Value)))
            .Select(token => token.CanonicalToken)
            .ToArray();
        return new FocusedEvidenceSelectionCoverage(uncovered, extraction.UnreadableReceiptPaths);
    }
}
