using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;

// Parallel-safe: pure counting and formatting with no shared state.
public sealed class OwnerConsoleOverflowTests
{
    [Theory]
    [InlineData(0, 0, 5, 0, 0)]
    [InlineData(5, 0, 5, 0, 0)]
    [InlineData(20, 3, 0, 0, 0)]
    [InlineData(20, 0, -1, 0, 0)]
    [InlineData(6, 0, 5, 0, 1)]
    [InlineData(6, 1, 5, 1, 0)]
    [InlineData(20, 3, 5, 3, 12)]
    [InlineData(20, -3, 5, 0, 15)]
    [InlineData(20, 25, 5, 20, 0)]
    public void Of_VisibleRange_CountsOnlyLoadedRowsOutsideIt(
        int count, int firstVisible, int visibleRows, int above, int below) =>
        Assert.Equal(new OwnerConsoleOverflow(above, below), OwnerConsoleOverflow.Of(count, firstVisible, visibleRows));

    [Theory]
    [InlineData(0, 0, 100, "ACTIVITY")]
    [InlineData(0, 0, 1, "ACTIVITY")]
    [InlineData(0, 40, 100, "ACTIVITY · 40 below")]
    [InlineData(12, 0, 100, "ACTIVITY · 12 above")]
    [InlineData(12, 40, 100, "ACTIVITY · 12 above · 40 below")]
    [InlineData(12, 40, 30, "ACTIVITY · 12 above · 40 below")]
    [InlineData(12, 40, 29, "ACTIVITY ^12 v40")]
    [InlineData(12, 40, 10, "A… ^12 v40")]
    [InlineData(12, 40, 0, "ACTIVITY · 12 above · 40 below")]
    [InlineData(12, 40, -4, "ACTIVITY · 12 above · 40 below")]
    public void Title_AvailableColumns_UsesLongShortOrCutBase(
        int above, int below, int columns, string expected) =>
        Assert.Equal(expected, OwnerConsoleOverflow.Title("ACTIVITY", new(above, below), columns));

    [Fact]
    public void Title_WideUnicodeBase_CutsByTerminalCells()
    {
        var title = OwnerConsoleOverflow.Title("界界界", new(12, 40), 12);
        Assert.Equal("界… ^12 v40", title);
        Assert.Equal(11, title.GetColumns());
    }
}
