using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Fit in terminal cells, cutting title spans before the prefix or outcome. The model retains full text.
internal static class OwnerConsoleLineFitter
{
    internal static string Fit(string line, IEnumerable<string> titles, int width)
    {
        if (width <= 0 || line.GetColumns() <= width) return line;
        foreach (var title in titles.Where(title => title.Length > 0).Distinct().OrderByDescending(title => title.Length))
        {
            var start = line.IndexOf(title, StringComparison.Ordinal);
            if (start < 0) continue;
            var available = Math.Max(1, title.GetColumns() - (line.GetColumns() - width));
            line = line[..start] + Cut(title, available) + line[(start + title.Length)..];
            if (line.GetColumns() <= width) return line;
        }
        return Cut(line, width);
    }

    internal static string Cut(string text, int width)
    {
        if (width <= 0) return "";
        if (text.GetColumns() <= width) return text;
        var result = new StringBuilder();
        var cells = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (cells + size > width - 1) break;
            result.Append(rune);
            cells += size;
        }
        return result + "…";
    }
}
