using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OwnedBriefOwnershipSettlement
{
    internal const string ForkKind = "ownership-lifecycle";

    internal sealed record Settlement(string AnswerText, IReadOnlyList<string> Citations);

    internal static bool TrySettle(string? forkKind, string? question, string? objective,
        out Settlement settlement)
    {
        settlement = null!;
        if (!string.Equals(forkKind?.Trim(), ForkKind, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(objective) ||
            question?.Contains("Revise the versioned criterion or its declared_index to resolve this ownership issue.",
                StringComparison.OrdinalIgnoreCase) == true)
            return false;

        var criteria = DeclaredTestRemovalSet.FromObjective(objective).Ordinary;
        if (criteria.Count == 0 || criteria.Any(criterion =>
                AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion).Kind == DeclaredEvidenceOwnerKind.None ||
                AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(criterion) ||
                AcceptanceCriterionOwnershipMarker.Classify(criterion).Classification is
                    AcceptanceCriterionOwnershipClassification.OperatorOwned or
                    AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal))
            return false;

        // Cite the containing criterion: short owner sentences alone may not meet the
        // brief-grounding minimum span length.
        var citations = criteria.Select(criterion => Regex.Replace(criterion, @"\s+", " ").Trim())
            .Distinct(StringComparer.Ordinal).ToArray();
        var answer = "The conductor and the acceptance gate execute verification; workers name the tests and the conductor runs them. " +
            "Operator steps after landing are observations beyond the numbered criteria and never gate completion. " +
            "Each criterion declares its owner in the brief:\n" +
            string.Join("\n", citations.Select(citation => $"\"{citation}\""));
        settlement = new(answer, citations);
        return true;
    }
}
