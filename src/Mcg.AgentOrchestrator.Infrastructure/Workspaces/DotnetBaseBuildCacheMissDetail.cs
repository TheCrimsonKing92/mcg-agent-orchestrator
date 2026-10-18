namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBaseBuildCacheMissDetail
{
    internal static string DifferingComponents(string recorded, string current)
    {
        var before = Parse(recorded);
        var after = Parse(current);
        return string.Join("+", before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(name => !before.TryGetValue(name, out var left) ||
                !after.TryGetValue(name, out var right) || !StringComparer.Ordinal.Equals(left, right))
            .Order(StringComparer.Ordinal));

        static Dictionary<string, string> Parse(string components)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in components.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = token.IndexOf('=');
                values[separator < 0 ? token : token[..separator]] = separator < 0 ? "" : token[(separator + 1)..];
            }
            return values;
        }
    }

    internal static string? Describe(IReadOnlyList<(string EntryName, string? Components)> siblings, string current)
    {
        if (siblings.Count == 0) return null;
        var closest = siblings.Where(sibling => !string.IsNullOrEmpty(sibling.Components))
            .Select(sibling => (sibling.EntryName, Difference: DifferingComponents(sibling.Components!, current)))
            .OrderBy(sibling => sibling.Difference.Length == 0 ? 0 : sibling.Difference.Count(ch => ch == '+') + 1)
            .ThenBy(sibling => sibling.EntryName, StringComparer.Ordinal)
            .ToArray();
        return closest.Length == 0 ? "unrecorded" : closest[0].Difference;
    }
}
