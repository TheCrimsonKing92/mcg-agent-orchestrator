using System.Data;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBoardRenderer
{
    internal static string Preview(OwnerConsoleSelectedGoalPreview preview, int width,
        string? stage = null, TimeZoneInfo? zone = null) =>
        string.Join('\n', OwnerConsoleSelectedPreview.Lines(preview, stage, zone ?? TimeZoneInfo.Local, width));

    internal static void Render(DataTable table, TableView view, IReadOnlyList<OwnerConsoleBoardRow> rows,
        int paneWidth, int titleWidth, bool sectioned = false)
    {
        const int titleColumn = 2;
        var priorityColumn = titleColumn + 1;
        var statusColumn = titleColumn + 2;
        view.Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < rows.Count && rows[args.RowIndex].Dimmed
            ? new Scheme(view.GetScheme().Disabled) : null;
        var displayedCount = OwnerConsoleBoardSection.DisplayedRowCount(rows, sectioned);
        // Empty placeholders preserve goal indexes. Header text is drawn pane-wide, never projected into cells.
        var cells = rows.Take(displayedCount).Select(row => sectioned && row.IsDiagnostic
            ? new[] { "", "", "", "", "", "" }
            : new[] { row.GoalPrefix, row.Epic, row.Title, row.Priority, row.Stage, Age(row.Age) }).ToArray();
        if (view is OwnerConsoleBoardPane pane)
            pane.Headers = sectioned ? Enumerable.Range(0, displayedCount).Where(index => rows[index].IsDiagnostic)
                .ToDictionary(index => index, index => OwnerConsoleBoardSection.HeaderLine(rows[index])) : [];
        var widestTitle = cells.Select(row => row[titleColumn].GetColumns()).DefaultIfEmpty().Max();
        var widths = Enumerable.Range(0, table.Columns.Count).Select(column =>
            Math.Max(table.Columns[column].ColumnName.GetColumns(), cells.Select(row => row[column].GetColumns()).DefaultIfEmpty().Max())).ToArray();
        var showPriority = cells.Any(row => row[priorityColumn] is { Length: > 0 } and not "Normal");
        if (paneWidth > 0)
        {
            var statusHeaderWidth = table.Columns[statusColumn].ColumnName.GetColumns();
            var statusWanted = Math.Min(widths[statusColumn], Math.Max(statusHeaderWidth, paneWidth / 2));
            for (var column = 0; column < widths.Length; column++)
                if (column != titleColumn) widths[column] = Math.Min(widths[column], Math.Max(table.Columns[column].ColumnName.Length, paneWidth / 6));
            titleWidth = Math.Max(1, paneWidth - widths.Where((_, column) => column != titleColumn).Sum() - widths.Length - 1);
            if (!showPriority || titleWidth < Math.Min(OwnerConsoleLineFitter.MinimumTitleColumns, widestTitle))
            {
                showPriority = false;
                titleWidth = Math.Max(1, paneWidth - widths.Where((_, column) => column != titleColumn && column != priorityColumn).Sum() - (widths.Length - 1) - 1);
            }
            // Keep the existing PRIORITY hiding decision, then let STATUS use TITLE's spare space.
            var titleAndStatusWidth = titleWidth + widths[statusColumn];
            widths[statusColumn] = Math.Max(statusHeaderWidth, Math.Min(statusWanted,
                titleAndStatusWidth - Math.Min(OwnerConsoleLineFitter.MinimumTitleColumns, widestTitle)));
            titleWidth = Math.Max(1, titleAndStatusWidth - widths[statusColumn]);
        }
        if (titleWidth > 0) widths[titleColumn] = Math.Min(widths[titleColumn], titleWidth);
        view.MaxCellWidth = Math.Max(1, widths.Max());
        for (var column = 0; column < widths.Length; column++)
        {
            var style = view.Style.GetOrCreateColumnStyle(column);
            style.Visible = column != priorityColumn || showPriority;
            style.MaxWidth = Math.Max(1, widths[column]);
            style.MinWidth = style.MaxWidth;
        }
        table.Rows.Clear();
        foreach (var row in cells)
        {
            for (var column = 0; column < row.Length; column++)
                row[column] = OwnerConsoleLineFitter.Fit(row[column], OwnerConsoleLineSpans.None,
                    column == titleColumn ? titleWidth : paneWidth > 0 ? widths[column] : 0);
            table.Rows.Add(row);
        }
        view.Update();
    }

    internal static string Age(TimeSpan? span) => span is null ? "unknown" :
        span.Value.TotalMinutes < 1 ? $"{span.Value.TotalSeconds:0}s" : span.Value.TotalHours < 1 ? $"{span.Value.TotalMinutes:0}m" : $"{span.Value.TotalHours:0}h";
}
