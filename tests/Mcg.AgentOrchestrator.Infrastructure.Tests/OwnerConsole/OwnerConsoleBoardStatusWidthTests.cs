using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

// Parallel-safe: each test owns its table and uninitialized GUI instance; no terminal driver or shared state.
public sealed class OwnerConsoleBoardStatusWidthTests
{
    private const string LongStatus = "waiting for gate (1st in queue), 3m";

    [Theory]
    [InlineData(190, 103)]
    [InlineData(120, 33)]
    public void Board_LongStatus_RendersInFullAndCutsOnlyTitle(int paneWidth, int titleWidth)
    {
        var rows = Fixture();
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };

        OwnerConsoleBoardRenderer.Render(table, view, rows, paneWidth, 0);

        Assert.True(view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal(35, view.Style.GetOrCreateColumnStyle(4).MaxWidth);
        Assert.Equal(titleWidth, view.Style.GetOrCreateColumnStyle(2).MaxWidth);
        Assert.Equal(105, rows[0].Title.Length);
        Assert.Equal(rows[0].Title[..(titleWidth - 1)] + "…", table.Rows[0]["TITLE"]);
        Assert.Equal(rows[1].Title, table.Rows[1]["TITLE"]);
        for (var index = 0; index < rows.Length; index++)
        {
            Assert.Equal(rows[index].GoalPrefix, table.Rows[index]["GOAL"]);
            Assert.Equal(rows[index].Epic, table.Rows[index]["EPIC"]);
            Assert.Equal(rows[index].Priority, table.Rows[index]["PRIORITY"]);
            Assert.Equal(rows[index].Stage, table.Rows[index]["STATUS"]);
        }
        Assert.Equal("unknown", table.Rows[0]["AGE"]);
        Assert.Equal("12345678h", table.Rows[1]["AGE"]);
        AssertVisibleCellsFit(table, view, paneWidth);
    }

    [Fact]
    public void Board_NarrowPane_KeepsTitleMinimumAndCutsStatus()
    {
        var rows = Fixture(allNormal: true);
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };

        OwnerConsoleBoardRenderer.Render(table, view, rows, 60, 0);

        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.All(rows, row => Assert.Equal("Normal", row.Priority));
        var titleWidth = view.Style.GetOrCreateColumnStyle(2).MaxWidth;
        Assert.Equal(OwnerConsoleLineFitter.MinimumTitleColumns, titleWidth);
        Assert.Equal(titleWidth, Assert.IsType<string>(table.Rows[0]["TITLE"]).GetColumns());
        Assert.Equal(rows[0].Title[..(titleWidth - 1)] + "…", table.Rows[0]["TITLE"]);
        Assert.Equal(11, view.Style.GetOrCreateColumnStyle(4).MaxWidth);
        Assert.Equal(LongStatus[..10] + "…", table.Rows[0]["STATUS"]);
        Assert.Equal(rows[1].Title, table.Rows[1]["TITLE"]);
        AssertVisibleCellsFit(table, view, 60);
    }

    [Theory]
    [InlineData(190)]
    [InlineData(120)]
    public void Board_LongStatus_LeavesGoalEpicAndAgeWidthsUnchanged(int paneWidth)
    {
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };

        OwnerConsoleBoardRenderer.Render(table, view, Fixture(), paneWidth, 0);

        // Pre-change widths for these same fixture values at both pane widths.
        Assert.Equal(8, view.Style.GetOrCreateColumnStyle(0).MaxWidth);
        Assert.Equal(20, view.Style.GetOrCreateColumnStyle(1).MaxWidth);
        Assert.Equal(9, view.Style.GetOrCreateColumnStyle(5).MaxWidth);
        AssertVisibleCellsFit(table, view, paneWidth);
    }

    [Theory]
    [InlineData(190, 95)]
    [InlineData(200, 100)]
    public void Board_OversizedStatus_StopsAtHalfPaneWidth(int paneWidth, int statusWidth)
    {
        var rows = Fixture();
        rows[0] = rows[0] with { Stage = new string('S', 200) };
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };

        OwnerConsoleBoardRenderer.Render(table, view, rows, paneWidth, 0);

        Assert.Equal(statusWidth, view.Style.GetOrCreateColumnStyle(4).MaxWidth);
        Assert.Equal(new string('S', statusWidth - 1) + "…", table.Rows[0]["STATUS"]);
        Assert.Equal(rows[1].Stage, table.Rows[1]["STATUS"]);
        Assert.True(view.Style.GetOrCreateColumnStyle(2).MaxWidth >= OwnerConsoleLineFitter.MinimumTitleColumns);
        AssertVisibleCellsFit(table, view, paneWidth);
    }

    private static OwnerConsoleBoardRow[] Fixture(bool allNormal = false) =>
    [
        new("11111111", "Owner console layout", new string('T', 105), "Verified", LongStatus,
            null, "11111111", Priority: allNormal ? "Normal" : "Critical"),
        // A nine-column age makes the 105-column TITLE require truncation even at width 190.
        new("22222222", "Owner console layout", "Short title", "Active", "in progress: Developer",
            TimeSpan.FromHours(12_345_678), "22222222", Priority: "Normal")
    ];

    private static DataTable BoardTable()
    {
        var table = new DataTable();
        foreach (var column in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" })
            table.Columns.Add(column);
        return table;
    }

    private static void AssertVisibleCellsFit(DataTable table, TableView view, int paneWidth)
    {
        var columns = Enumerable.Range(0, table.Columns.Count)
            .Where(column => view.Style.GetOrCreateColumnStyle(column).Visible).ToArray();
        Assert.Equal(paneWidth, columns.Sum(column => view.Style.GetOrCreateColumnStyle(column).MaxWidth)
            + columns.Length + 1);
        foreach (var column in columns)
        {
            var style = view.Style.GetOrCreateColumnStyle(column);
            Assert.Equal(style.MaxWidth, style.MinWidth);
            foreach (DataRow row in table.Rows)
                Assert.True(Assert.IsType<string>(row[column]).GetColumns() <= style.MaxWidth,
                    $"Cell {table.Columns[column].ColumnName} exceeds its allocated width.");
        }
    }
}
