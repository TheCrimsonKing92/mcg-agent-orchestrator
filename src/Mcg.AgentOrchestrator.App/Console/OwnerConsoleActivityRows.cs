using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleActivityRow(string Text, int? ItemIndex, OwnerConsoleActivityKey Key);

internal static class OwnerConsoleActivityRows
{
    internal static IReadOnlyList<OwnerConsoleActivityRow> Build(IReadOnlyList<OwnerConsoleActivityItem> items)
    {
        var keys = OwnerConsoleActivityFollow.Keys(items);
        var dates = items.Select(item => DateOnly.FromDateTime(item.Timestamp.ToLocalTime().DateTime)).ToArray();
        if (dates.Distinct().Count() < 2)
            return items.Select((item, index) => new OwnerConsoleActivityRow(
                OwnerActivityNarrator.Line(item), index, keys[index])).ToArray();

        var rows = new List<OwnerConsoleActivityRow>();
        for (var index = 0; index < items.Count; index++)
        {
            if (index == 0 || dates[index] != dates[index - 1])
            {
                var text = Separator(dates[index]);
                rows.Add(new(text, null, new(default, "day-separator", "", text, null, 0)));
            }
            rows.Add(new(OwnerActivityNarrator.Line(items[index]), index, keys[index]));
        }
        return rows;
    }

    internal static string Separator(DateOnly date) =>
        "-- " + date.ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture) + " --";
}
