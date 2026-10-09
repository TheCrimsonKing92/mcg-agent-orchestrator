using System.Data;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleBoardRenderer
{
    internal static void Render(DataTable table, TableView view, IReadOnlyList<OwnerConsoleBoardRow> rows,
        int paneWidth, int titleWidth)
    {
        view.Style.RowColorGetter = args => args.RowIndex >= 0 && args.RowIndex < rows.Count && rows[args.RowIndex].Dimmed
            ? new Scheme(view.GetScheme().Disabled) : null;
        var cells = rows.Select(row => new[] { row.GoalPrefix, row.Epic, row.Title, row.State, row.Stage, Age(row.Age) }).ToArray();
        var widths = Enumerable.Range(0, table.Columns.Count).Select(column =>
            Math.Max(table.Columns[column].ColumnName.GetColumns(), cells.Select(row => row[column].GetColumns()).DefaultIfEmpty().Max())).ToArray();
        if (paneWidth > 0)
        {
            for (var column = 0; column < widths.Length; column++)
                if (column != 2) widths[column] = Math.Min(widths[column], Math.Max(table.Columns[column].ColumnName.Length, paneWidth / 6));
            titleWidth = Math.Max(1, paneWidth - widths.Where((_, column) => column != 2).Sum() - widths.Length - 1);
        }
        if (titleWidth > 0) widths[2] = Math.Min(widths[2], titleWidth);
        view.MaxCellWidth = Math.Max(1, widths.Max());
        for (var column = 0; column < widths.Length; column++)
        {
            var style = view.Style.GetOrCreateColumnStyle(column);
            style.MaxWidth = Math.Max(1, widths[column]);
            style.MinWidth = style.MaxWidth;
        }
        table.Rows.Clear();
        foreach (var row in cells)
        {
            for (var column = 0; column < row.Length; column++)
                row[column] = OwnerConsoleLineFitter.Fit(row[column], OwnerConsoleLineSpans.None,
                    column == 2 ? titleWidth : paneWidth > 0 ? widths[column] : 0);
            table.Rows.Add(row);
        }
        view.Update();
    }

    private static string Age(TimeSpan? span) => span is null ? "unknown" :
        span.Value.TotalMinutes < 1 ? $"{span.Value.TotalSeconds:0}s" : span.Value.TotalHours < 1 ? $"{span.Value.TotalMinutes:0}m" : $"{span.Value.TotalHours:0}h";
}
