namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredTestClosestIdentities
{
    internal static IReadOnlyList<string> Find(IEnumerable<string> selectors,
        IReadOnlyList<DeclaredTestReportRows.Row> rows)
    {
        var requested = selectors.ToArray();
        if (requested.Length == 0) return [];
        return rows.Select(row => row.Identity).Distinct(StringComparer.Ordinal)
            .OrderBy(identity => requested.Min(selector => Distance(selector, identity)))
            .ThenBy(identity => identity, StringComparer.Ordinal).Take(3).ToArray();
    }

    private static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
