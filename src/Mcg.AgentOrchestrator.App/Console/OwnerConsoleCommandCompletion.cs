using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleCompletion(string Text, IReadOnlyList<OwnerConsoleBinding> Candidates, string Hint);

internal static class OwnerConsoleCommandCompletion
{
    internal static OwnerConsoleCompletion Complete(string text, IEnumerable<OwnerConsoleBinding> commands, int width)
    {
        width = Math.Max(20, width);
        var input = text.TrimStart();
        var prefix = input.StartsWith(':') ? ":" : string.Empty;
        if (prefix.Length > 0) input = input[1..].TrimStart();
        input = Regex.Replace(input, " +", " ");
        var rows = commands.ToArray();
        var candidates = rows.Where(row => Literal(row).StartsWith(input, StringComparison.OrdinalIgnoreCase) ||
            HasPlaceholder(row) && input.StartsWith(Literal(row) + " ", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0 && input.Contains(' '))
            candidates = rows.Where(row => row.Command.Equals(input.Split(' ')[0], StringComparison.OrdinalIgnoreCase)).ToArray();

        if (candidates.Length == 1)
        {
            var row = candidates[0];
            var literal = Literal(row);
            var completed = input.Length < literal.Length && literal.StartsWith(input, StringComparison.OrdinalIgnoreCase)
                ? prefix + literal + (HasPlaceholder(row) ? " " : "") : text;
            var hint = $"{row.Usage}: {OwnerConsoleBindings.Description(row)}";
            return new(completed, candidates, hint.Length <= width ? hint : hint[..(width - 3)] + "...");
        }

        if (candidates.Length == 0)
            return new(text, candidates, ListHint($"no command starts with \"{input}\"; commands: ", rows, width));

        var common = Literal(candidates[0]);
        foreach (var row in candidates.Skip(1))
        {
            var literal = Literal(row);
            var length = 0;
            while (length < common.Length && length < literal.Length &&
                char.ToUpperInvariant(common[length]) == char.ToUpperInvariant(literal[length])) length++;
            common = common[..length];
        }
        return new(common.Length > input.Length && common.StartsWith(input, StringComparison.OrdinalIgnoreCase) ? prefix + common : text, candidates,
            ListHint($"matches ({candidates.Length}): ", candidates, width));
    }

    private static string Literal(OwnerConsoleBinding row) => row.Usage.Split(" <", 2, StringSplitOptions.None)[0];
    private static bool HasPlaceholder(OwnerConsoleBinding row) => row.Usage.Contains(" <", StringComparison.Ordinal);

    private static string ListHint(string header, IReadOnlyList<OwnerConsoleBinding> rows, int width)
    {
        var entries = rows.Select(row => row.Key is { } key ? $"[{key}] {row.Usage}" : row.Usage).ToArray();
        var all = header + string.Join(" | ", entries);
        if (all.Length <= width) return all;

        var shown = 0;
        var length = header.Length;
        for (var count = 1; count < entries.Length; count++)
        {
            length += entries[count - 1].Length + (count > 1 ? 3 : 0);
            if (length + $" | +{entries.Length - count} more".Length <= width) shown = count;
        }
        var suffix = $"+{entries.Length - shown} more";
        if (shown == 0)
            return header[..Math.Min(header.Length, width - suffix.Length)] + suffix;
        return header + string.Join(" | ", entries.Take(shown)) + " | " + suffix;
    }
}
