using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal readonly record struct OwnerConsoleOverflow(int Above, int Below)
{
    // Two corners and the two title caps occupy four frame cells around the title.
    internal const int TitleChrome = 4;

    internal static OwnerConsoleOverflow Of(int count, int firstVisible, int visibleRows) =>
        visibleRows <= 0 || count <= visibleRows ? default : new(
            Math.Clamp(firstVisible, 0, count),
            Math.Max(0, count - Math.Max(0, firstVisible) - visibleRows));

    internal static string Title(string baseTitle, OwnerConsoleOverflow overflow, int columns)
    {
        if (overflow == default) return baseTitle;
        var longSuffix = (overflow.Above > 0 ? $" · {overflow.Above} above" : "") +
            (overflow.Below > 0 ? $" · {overflow.Below} below" : "");
        if (columns <= 0 || baseTitle.GetColumns() + longSuffix.GetColumns() <= columns)
            return baseTitle + longSuffix;
        var shortSuffix = (overflow.Above > 0 ? $" ^{overflow.Above}" : "") +
            (overflow.Below > 0 ? $" v{overflow.Below}" : "");
        if (baseTitle.GetColumns() + shortSuffix.GetColumns() <= columns) return baseTitle + shortSuffix;

        var width = Math.Max(1, columns - shortSuffix.GetColumns() - 1);
        var result = new StringBuilder();
        var cells = 0;
        foreach (var rune in baseTitle.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (cells + size > width) break;
            result.Append(rune);
            cells += size;
        }
        return result + "…" + shortSuffix;
    }
}
