using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleActivityFilter
{
    internal const string EmptyKind = "filter-empty";
    internal const string ClearedNotice = "ACTIVITY filter cleared";

    internal static string? Normalize(string? text) =>
        text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    internal static bool Matches(OwnerConsoleActivityItem item, string filter) =>
        (item.GoalPrefix.Length > 0 && item.GoalPrefix.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) ||
        (item.GoalPrefix.Length == 8 && filter.Length > 8 && filter.StartsWith(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)) ||
        OwnerActivityNarrator.Line(item).Contains(filter, StringComparison.OrdinalIgnoreCase);

    internal static string AppliedNotice(string filter, int matching, int total) =>
        $"ACTIVITY filter \"{filter}\": {matching} of {total} rows";
    internal static string EmptyText(string filter) =>
        $"No ACTIVITY row matches filter \"{filter}\"; type :filter to clear it.";
    internal static string EmptyEnterNotice(string filter) =>
        $"Enter: no ACTIVITY row matches filter \"{filter}\"; type :filter to clear it";
    internal static string CommandText(string? filter) => "filter " + filter;
    internal static string LongTitle(string filter) => $" · filter \"{filter}\"";
    internal static string ShortTitle(string filter)
    {
        if (filter.GetColumns() <= 12) return "/" + filter + " ";
        var text = new StringBuilder("/");
        var columns = 0;
        foreach (var rune in filter.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (columns + size > 11) break;
            text.Append(rune);
            columns += size;
        }
        return text + "… ";
    }

    internal static IReadOnlyList<OwnerConsoleActivityRow> Rows(
        IReadOnlyList<OwnerConsoleActivityItem> items, string filter)
    {
        var keys = OwnerConsoleActivityFollow.Keys(items);
        var matching = Enumerable.Range(0, items.Count).Where(index => Matches(items[index], filter)).ToArray();
        if (matching.Length == 0)
        {
            var text = EmptyText(filter);
            return [new(text, null, new(default, EmptyKind, "", text, null, 0))];
        }
        var dates = matching.Select(index => DateOnly.FromDateTime(items[index].Timestamp.ToLocalTime().DateTime)).ToArray();
        var separators = dates.Distinct().Count() > 1;
        var rows = new List<OwnerConsoleActivityRow>();
        for (var position = 0; position < matching.Length; position++)
        {
            if (separators && (position == 0 || dates[position] != dates[position - 1]))
            {
                var text = OwnerConsoleActivityRows.Separator(dates[position]);
                rows.Add(new(text, null, new(default, "day-separator", "", text, null, 0)));
            }
            var index = matching[position];
            rows.Add(new(OwnerActivityNarrator.Line(items[index]), index, keys[index]));
        }
        return rows;
    }
}
