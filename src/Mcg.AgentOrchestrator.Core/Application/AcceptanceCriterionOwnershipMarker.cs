using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public enum AcceptanceCriterionOwnershipClassification
{
    Unclassified,
    NotOperatorOwned,
    OperatorOwned,
    OperatorOwnedWeakSignal
}

public sealed record AcceptanceCriterionOwnershipMarkerResult(
    AcceptanceCriterionOwnershipClassification Classification,
    string TrailingRegion,
    string? ConflictingDeclaredOwner = null);

public static partial class AcceptanceCriterionOwnershipMarker
{
    private const int MaximumTrailingSentences = 4;

    public static AcceptanceCriterionOwnershipMarkerResult Classify(string? criterion)
    {
        var trailingSentences = ExtractTrailingSentences(criterion);
        var trailingRegion = string.Join(" ", trailingSentences);
        if (trailingRegion.Length == 0)
            return new(AcceptanceCriterionOwnershipClassification.Unclassified, trailingRegion);

        var explicitOwners = trailingSentences
            .SelectMany(sentence => ExplicitOwnerSentenceRegex().Matches(sentence).Cast<Match>())
            .ToArray();
        if (explicitOwners.Length > 0)
        {
            var explicitNonOperatorOwner = explicitOwners.FirstOrDefault(match =>
                    !match.Groups["owner"].Value.Equals("operator", StringComparison.OrdinalIgnoreCase) &&
                    match.Groups["verb"].Value.Equals("owns", StringComparison.OrdinalIgnoreCase))
                ?? explicitOwners.FirstOrDefault(match =>
                    !match.Groups["owner"].Value.Equals("operator", StringComparison.OrdinalIgnoreCase));
            return new(
                explicitOwners.Any(match => match.Groups["owner"].Value.Equals("operator", StringComparison.OrdinalIgnoreCase))
                    ? AcceptanceCriterionOwnershipClassification.OperatorOwned
                    : AcceptanceCriterionOwnershipClassification.NotOperatorOwned,
                trailingRegion,
                explicitNonOperatorOwner?.Groups["owner"].Value);
        }

        var operatorOwned = OperatorOwnershipRegex().IsMatch(trailingRegion);
        var nonOperatorOwner = NonOperatorOwnershipRegex().Match(trailingRegion);
        if (operatorOwned)
        {
            return new(
                AcceptanceCriterionOwnershipClassification.OperatorOwned,
                trailingRegion,
                nonOperatorOwner.Success ? nonOperatorOwner.Groups["owner"].Value : null);
        }

        if (nonOperatorOwner.Success)
        {
            return new(
                AcceptanceCriterionOwnershipClassification.NotOperatorOwned,
                trailingRegion,
                nonOperatorOwner.Groups["owner"].Value);
        }

        return RealWorldDependentRegex().IsMatch(trailingRegion)
            ? new(AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal, trailingRegion)
            : new(AcceptanceCriterionOwnershipClassification.Unclassified, trailingRegion);
    }

    public static bool HasOperatorOwnershipPhrase(string? text) =>
        !string.IsNullOrWhiteSpace(text) && OperatorOwnershipRegex().IsMatch(text);

    public static bool HasAcceptanceGateOwnershipMarker(string? text) =>
        !string.IsNullOrWhiteSpace(text) && AcceptanceGateOwnershipRegex().IsMatch(text);

    public static string ExtractTrailingRegion(string? criterion) =>
        string.Join(" ", ExtractTrailingSentences(criterion));

    private static string[] ExtractTrailingSentences(string? criterion)
    {
        if (string.IsNullOrWhiteSpace(criterion))
            return [];

        var sentences = SentenceBoundaryRegex()
            .Split(criterion.Trim())
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToArray();
        var firstIncluded = sentences.Length;
        for (var index = sentences.Length - 1;
             index >= 0 && sentences.Length - index <= MaximumTrailingSentences;
             index--)
        {
            if (!TrailingRegionSignalRegex().IsMatch(sentences[index]))
                break;
            firstIncluded = index;
        }

        return firstIncluded == sentences.Length
            ? []
            : sentences[firstIncluded..];
    }

    [GeneratedRegex(
        @"(?:^|;)\s*(?:the\s+)?(?<owner>operator|developer|researcher|reviewer|tester|planner|acceptance(?:[\s-]+gate)?|gate)\s+(?<verb>owns|executes)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOwnerSentenceRegex();

    [GeneratedRegex(@"(?<=[.!?])(?:\s+|$)|[\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(
        @"\b(?:owns?|owned)\b|TEST-VERIFIABLE|REAL-WORLD-DEPENDENT",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRegionSignalRegex();

    [GeneratedRegex(
        @"\b(?:(?:the\s+)?operator(?:\s*-\s*|\s+)owned|(?:the\s+)?operator\s+owns|owned\s+by\s+(?:the\s+)?operator)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OperatorOwnershipRegex();

    [GeneratedRegex(
        @"\b(?<owner>developer|researcher|reviewer|tester|planner|acceptance)(?:\s+owns|\s+owned|\s*-\s*owned)\b|\b(?<owner>acceptance-gate|gate)(?:\s+owned|\s*-\s*owned)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonOperatorOwnershipRegex();

    [GeneratedRegex(
        @"\b(?:Acceptance\s+executes|ACCEPTANCE-GATE-OWNED|executed\s+by\s+the\s+acceptance\s+gate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceGateOwnershipRegex();

    [GeneratedRegex(@"\bREAL-WORLD-DEPENDENT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RealWorldDependentRegex();
}
