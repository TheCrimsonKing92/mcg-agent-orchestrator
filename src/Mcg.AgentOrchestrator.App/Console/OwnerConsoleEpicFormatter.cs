using Mcg.AgentOrchestrator.App.Cli;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleEpicFormatter
{
    internal const int DefaultWidth = 100;
    internal const string KeyHint = "e epics  :epics";
    internal const string ListHint = "Up/Down select  Enter detail  w window  Esc close";
    internal const string DetailHint = "Up/Down scroll  PgUp/PgDn page  w window  Esc epics";

    internal static string Header(OwnerConsoleEpicWindow window) => "EPICS · " + (window switch
    {
        OwnerConsoleEpicWindow.Day => "last 24 hours",
        OwnerConsoleEpicWindow.Week => "last 7 days",
        _ => "all time"
    });

    internal static string Summary(EpicProgressRollup row) =>
        $"landed {row.WindowLandedCount ?? row.LandedCount} · in flight {row.VerifyingCount + row.ActiveCount} ({row.VerifyingCount} verifying, {row.ActiveCount} active) · failed {row.WindowFailedCount ?? row.FailedCount} · backlog {row.BacklogOpenCount} open / {row.BacklogDoneCount} done";

    internal static IReadOnlyList<string> ListLines(OwnerConsoleEpicViewModel model, int width, string? selectedId)
    {
        width = Width(width);
        if (model.Epics.Count == 0) return Wrap("No epics.", width).ToArray();
        var lines = new List<string>();
        foreach (var row in model.Epics)
        {
            var marker = row.Epic.Id == selectedId ? "> " : "  ";
            lines.Add(Fit(marker + Title(row.Epic.Title), width));
            lines.AddRange(Wrap(Summary(row), width));
            lines.Add(string.Empty);
        }
        return lines;
    }

    internal static IReadOnlyList<string> DetailLines(OwnerConsoleEpicViewModel.Detail detail, int width)
    {
        width = Width(width);
        var lines = new List<string> { Fit(Title(detail.Epic.Title), width), "Plan of record:" };
        lines.AddRange(Wrap(string.IsNullOrWhiteSpace(detail.Epic.Description) ? "(no plan of record)" : detail.Epic.Description, width));
        Section("In flight", detail.InFlight);
        Section("Landed in window", detail.Landed);
        Section("Failed", detail.Failed);
        Section("Parked", detail.Parked);
        return lines.SelectMany(line => Wrap(line, width)).ToArray();

        void Section(string title, IReadOnlyList<OwnerConsoleEpicViewModel.GoalLine> goals)
        {
            lines.Add(string.Empty);
            lines.Add(title + ":");
            if (goals.Count == 0) lines.Add("(none)");
            foreach (var goal in goals)
            {
                var prefix = goal.Id[..Math.Min(8, goal.Id.Length)];
                var fixedWidth = prefix.Length + goal.Stage.Length + 4;
                var titleWidth = width - fixedWidth;
                if (titleWidth > 0)
                    lines.Add($"{prefix}  {Fit(Title(goal.Title), titleWidth)}  {goal.Stage}");
                else
                {
                    // Preserve prefix and stage even on a viewport narrower than those fields.
                    lines.AddRange(Wrap($"{prefix}  {goal.Stage}", width));
                    lines.Add(Fit(Title(goal.Title), width));
                }
                if (goal.Reason is { } reason) lines.AddRange(Wrap(reason, width));
            }
        }
    }

    internal static int Width(int width) => width > 0 ? width : DefaultWidth;
    private static string Title(string text) => text.Trim().TrimStart('#').Trim();
    private static string Fit(string text, int width) => text.GetColumns() <= width ? text :
        text[..PrefixLength(text, Math.Max(0, width - 1))] + "…";

    private static int PrefixLength(string text, int width)
    {
        var length = 0;
        var columns = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = Math.Max(0, rune.GetColumns());
            if (columns + size > width) break;
            length += rune.Utf16SequenceLength;
            columns += size;
        }
        return length;
    }

    // Keep all description and fixed-field text, including paragraphs and long unbroken tokens.
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        width = Width(width);
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var rest = paragraph;
            while (rest.GetColumns() > width)
            {
                var prefixLength = PrefixLength(rest, width);
                // A double-width rune cannot fit a one-column terminal, but must remain reachable.
                if (prefixLength == 0) prefixLength = System.Text.Rune.GetRuneAt(rest, 0).Utf16SequenceLength;
                var space = rest.LastIndexOf(' ', prefixLength - 1, prefixLength);
                var cut = space > 0 ? space : prefixLength;
                yield return rest[..cut];
                rest = rest[(space > 0 ? cut + 1 : cut)..];
            }
            yield return rest;
        }
    }
}
