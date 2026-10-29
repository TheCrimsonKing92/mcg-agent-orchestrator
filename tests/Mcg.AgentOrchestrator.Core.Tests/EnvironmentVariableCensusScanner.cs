using System.Text.RegularExpressions;

internal static class EnvironmentVariableCensusScanner
{
    // Full-text discovery also covers constants and raw/interpolated literal portions.
    // A comment-only name is conservatively included rather than silently omitted.
    private static readonly Regex SourceName = new("MCG_[A-Z0-9_]+", RegexOptions.CultureInvariant);
    private static readonly Regex TableName = new("^MCG_[A-Z0-9_]+$", RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> FindUnlisted(IEnumerable<string> sourceTexts, string tableText)
    {
        var listed = ReadRows(tableText).Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        return DiscoverNames(sourceTexts).Where(name => !listed.Contains(name)).ToArray();
    }

    internal static IReadOnlyList<string> DiscoverNames(IEnumerable<string> sourceTexts) => sourceTexts
        .SelectMany(source => SourceName.Matches(source).Select(match => match.Value))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    internal static IReadOnlyList<string[]> ReadRows(string tableText)
    {
        var rows = new List<string[]>();
        foreach (var line in tableText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
                continue;

            var cells = trimmed[1..^1].Split('|').Select(cell => cell.Trim()).ToArray();
            cells[0] = cells[0].Trim('`');
            if (TableName.IsMatch(cells[0]))
                rows.Add(cells);
        }
        return rows;
    }
}
