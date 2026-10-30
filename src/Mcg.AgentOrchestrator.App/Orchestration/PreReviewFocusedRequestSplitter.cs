using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class PreReviewFocusedRequestSplitter
{
    // Mirrors GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength. Broker validation tests guard drift.
    internal const int MaxFocusedEvidenceFilterLength = 1024;

    internal static bool TryResolveBrokerAlias(string project, out string? alias)
    {
        alias = project.Contains("Core.Tests", StringComparison.OrdinalIgnoreCase)
            ? "Core.Tests"
            : project.Contains("Infrastructure.Tests", StringComparison.OrdinalIgnoreCase)
                ? "Infrastructure.Tests"
                : null;
        return alias is not null;
    }

    internal static bool TrySplitRequestItems(
        string alias,
        string filterText,
        out IReadOnlyList<string> items)
    {
        var originalItem = BuildItem(alias, filterText);
        if (MeasuredExpressionLength(filterText) <= MaxFocusedEvidenceFilterLength)
        {
            items = [originalItem];
            return true;
        }

        var text = PreReviewPositiveUnionNormalizer.TryFlatten(filterText, out var flattened)
            ? flattened
            : filterText;
        var clauses = text.Split('|');
        if (!IsPositiveDisjunction(clauses) ||
            clauses.Any(clause => MeasuredExpressionLength(clause) > MaxFocusedEvidenceFilterLength))
        {
            items = [originalItem];
            return false;
        }

        // The broker counts the optional separator space before normalizing the filter. At the exact
        // boundary, omit that space so it cannot split here and then repack the same canonical filter.
        if (text.Length <= MaxFocusedEvidenceFilterLength)
        {
            items = [$"{alias}:{text}"];
            return true;
        }

        var splitItems = new List<string>();
        var current = clauses[0];
        foreach (var clause in clauses.Skip(1))
        {
            var candidate = $"{current}|{clause}";
            if (MeasuredExpressionLength(candidate) <= MaxFocusedEvidenceFilterLength)
            {
                current = candidate;
                continue;
            }

            splitItems.Add(BuildItem(alias, current));
            current = clause;
        }

        splitItems.Add(BuildItem(alias, current));
        items = splitItems;
        return splitItems.Count > 1;
    }

    internal static bool IsPositiveClause(string clause) => PositiveClausePattern().IsMatch(clause);

    private static bool IsPositiveDisjunction(IReadOnlyList<string> clauses) =>
        clauses.Count > 0 && clauses.All(IsPositiveClause);

    private static int MeasuredExpressionLength(string filterText) => 1 + filterText.Length;

    private static string BuildItem(string alias, string filterText) => $"{alias}: {filterText}";

    [GeneratedRegex(@"^FullyQualifiedName\s*~\s*[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveClausePattern();
}
