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

        // An explicit Acceptance declaration is authority in its own right. Source-role
        // ownership in the same criterion does not replace its execution-evidence owner.
        return explicitOwners.Any(match => IsAcceptanceOwner(match.Groups["owner"].Value)) ||
               AcceptanceGateOwnershipRegex().IsMatch(string.Join(" ", trailingSentences))
            ? new(DeclaredEvidenceOwnerKind.AcceptanceGate, null)
            : new(DeclaredEvidenceOwnerKind.Worker, explicitOwners[0].Groups["owner"].Value);
    }

    private static bool IsAcceptanceOwner(string owner) =>
        owner.Equals("gate", StringComparison.OrdinalIgnoreCase) ||
        owner.StartsWith("acceptance", StringComparison.OrdinalIgnoreCase);

    public static bool HasConflictingDeclaredEvidenceOwners(string? criterion)
    {
        var sentences = ExtractTrailingSentences(criterion);
        var owners = sentences
            .SelectMany(sentence => ExplicitOwnerSentenceRegex().Matches(sentence).Cast<Match>())
            .Select(match => match.Groups["owner"].Value)
            .ToArray();
        // Marker declarations may accompany explicit owner sentences. Require a
        // clause boundary so descriptive mentions do not become ownership claims.
        var clauses = sentences.SelectMany(sentence => sentence.Split(';'))
            .Select(clause => clause.Trim())
            .Select(clause => clause.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? clause[4..] : clause)
            .ToArray();
        var acceptanceDeclared = owners.Any(IsAcceptanceOwner) ||
                                 clauses.Any(clause => AcceptanceGateOwnershipRegex().Match(clause) is { Success: true, Index: 0 });
        var operatorDeclared = owners.Any(owner => owner.Equals("operator", StringComparison.OrdinalIgnoreCase)) ||
                               clauses.Any(clause => OperatorOwnershipRegex().Match(clause) is { Success: true, Index: 0 });
        return acceptanceDeclared && operatorDeclared;
    }
}
