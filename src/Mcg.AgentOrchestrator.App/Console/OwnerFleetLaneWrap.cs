using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerFleetLaneWrap
{
    internal static IReadOnlyList<string> Wrap(string text, int columns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 4);
        var rows = new List<string>();
        var remaining = text;
        var indent = "";
        while (indent.GetColumns() + remaining.GetColumns() > columns)
        {
            var budget = columns - indent.Length;
            var cells = 0;
            var end = 0;
            var space = -1;
            foreach (var rune in remaining.EnumerateRunes())
            {
                if (cells + rune.GetColumns() > budget) break;
                if (rune.Value == ' ' && cells > 0) space = end;
                cells += rune.GetColumns();
                end += rune.Utf16SequenceLength;
            }
            // A space immediately after the fitting prefix is also a word boundary.
            if (end < remaining.Length && remaining[end] == ' ') space = end;
            // Ignore leading indentation as a break point: a long first word must advance.
            var split = space > 0 && remaining[..space].Any(ch => ch != ' ') ? space : end;
            rows.Add(indent + remaining[..split]);
            remaining = remaining[split..].TrimStart(' ');
            indent = "  ";
        }
        rows.Add(indent + remaining);
        return rows;
    }
}
