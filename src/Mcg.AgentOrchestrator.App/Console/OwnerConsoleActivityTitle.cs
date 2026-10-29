using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleActivityTitle
{
    internal static string Title(OwnerConsoleOverflow overflow, bool following, int columns, string? filter = null,
        string? status = null)
    {
        filter = OwnerConsoleActivityFilter.Normalize(filter);
        var baseTitle = "ACTIVITY" + (status is null ? "" : $" ({status})");
        var longSuffix = (overflow.Above > 0 ? $" · {overflow.Above} above" : "") +
            (overflow.Below > 0 ? $" · {overflow.Below} below" : "");
        var longTitle = baseTitle + (filter is null ? "" : OwnerConsoleActivityFilter.LongTitle(filter)) + " · " + (following ? "following" : "inspecting") + longSuffix;
        if (columns <= 0 || longTitle.GetColumns() <= columns) return longTitle;

        var shortSuffix = (overflow.Above > 0 ? $" ^{overflow.Above}" : "") +
            (overflow.Below > 0 ? $" v{overflow.Below}" : "");
        var tail = " " + (filter is null ? "" : OwnerConsoleActivityFilter.ShortTitle(filter)) + (following ? "follow" : "inspect") + shortSuffix;
        if (baseTitle.GetColumns() + tail.GetColumns() <= columns) return baseTitle + tail;

        var width = Math.Max(1, columns - tail.GetColumns() - 1);
        var result = new StringBuilder();
        var cells = 0;
        foreach (var rune in baseTitle.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (cells + size > width) break;
            result.Append(rune);
            cells += size;
        }
        return result + "…" + tail;
    }
}
