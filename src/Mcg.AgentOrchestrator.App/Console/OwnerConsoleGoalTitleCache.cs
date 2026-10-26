using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGoalTitleCache(Func<string?, string>? title = null)
{
    private readonly Func<string?, string> _title = title ?? OwnerGoalTitle.Full;
    private ImmutableDictionary<string, (string Title, string UpdatedAt)> _entries =
        ImmutableDictionary.Create<string, (string, string)>(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _titles = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, string> Titles => _titles;

    internal void Merge(IReadOnlyList<GoalSummary> rows)
    {
        var entries = _entries;
        var titles = _titles;
        foreach (var row in rows)
        {
            if (entries.TryGetValue(row.Id, out var entry) && entry.UpdatedAt == row.UpdatedAt) continue;
            var value = _title(row.Objective);
            entries = entries.SetItem(row.Id, (value, row.UpdatedAt));
            titles = titles.SetItem(row.Id, value);
        }
        _entries = entries;
        _titles = titles;
    }

    internal string Title(string? idOrPrefix)
    {
        if (string.IsNullOrEmpty(idOrPrefix)) return string.Empty;
        var titles = _titles;
        var exact = titles.FirstOrDefault(item => item.Key.Equals(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact.Key is not null) return exact.Value;
        var matches = titles.Where(item => item.Key.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : string.Empty;
    }
}
