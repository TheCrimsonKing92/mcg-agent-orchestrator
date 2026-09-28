using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public enum DeclaredEvidenceOwnerKind
{
    None,
    AcceptanceGate,
    Worker
}

public readonly record struct DeclaredEvidenceOwner(DeclaredEvidenceOwnerKind Kind, string? Role)
{
    public static DeclaredEvidenceOwner None => new(DeclaredEvidenceOwnerKind.None, null);

    public string WireToken => Kind switch
    {
        DeclaredEvidenceOwnerKind.AcceptanceGate => "acceptance-gate",
        DeclaredEvidenceOwnerKind.Worker => "worker",
        _ => throw new InvalidOperationException("An undeclared owner has no evidence-owner token.")
    };
}

public static partial class AcceptanceCriterionOwnershipMarker
{
    public static DeclaredEvidenceOwner ResolveDeclaredEvidenceOwner(string? criterion)
    {
        var trailingSentences = ExtractTrailingSentences(criterion);
        var explicitOwners = trailingSentences
            .SelectMany(sentence => ExplicitOwnerSentenceRegex().Matches(sentence).Cast<Match>())
            .ToArray();
        if (explicitOwners.Length == 0 || explicitOwners.Any(match =>
                match.Groups["owner"].Value.Equals("operator", StringComparison.OrdinalIgnoreCase)))
        {
            return DeclaredEvidenceOwner.None;
        }

        return AcceptanceGateOwnershipRegex().IsMatch(string.Join(" ", trailingSentences))
            ? new(DeclaredEvidenceOwnerKind.AcceptanceGate, null)
            : new(DeclaredEvidenceOwnerKind.Worker, explicitOwners[0].Groups["owner"].Value);
    }
}
