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
                : project.Contains("Dashboard.Tests", StringComparison.OrdinalIgnoreCase)
                    ? "Dashboard.Tests"
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

        var clauses = filterText.Split('|');
        if (!IsPositiveDisjunction(clauses) ||
            clauses.Any(clause => BuildItem(alias, clause).Length > MaxFocusedEvidenceFilterLength))
        {
            items = [originalItem];
            return false;
        }

        var splitItems = new List<string>();
        var current = clauses[0];
        foreach (var clause in clauses.Skip(1))
        {
            var candidate = $"{current}|{clause}";
            if (BuildItem(alias, candidate).Length <= MaxFocusedEvidenceFilterLength)
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

    private static bool IsPositiveDisjunction(IReadOnlyList<string> clauses) =>
        clauses.Count > 1 &&
        clauses.All(clause => PositiveClausePattern().IsMatch(clause));

    private static int MeasuredExpressionLength(string filterText) => 1 + filterText.Length;

    private static string BuildItem(string alias, string filterText) => $"{alias}: {filterText}";

    [GeneratedRegex(@"^FullyQualifiedName\s*~\s*[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveClausePattern();
}
