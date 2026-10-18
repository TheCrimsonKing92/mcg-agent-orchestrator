using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;

// Parallel-safe: pure formatting with no shared state.
public sealed class OwnerConsoleActivityTitleTests
{
    [Theory]
    [InlineData(0, 0, true, 100, "ACTIVITY · following")]
    [InlineData(0, 0, false, 100, "ACTIVITY · inspecting")]
    [InlineData(0, 0, true, 20, "ACTIVITY · following")]
    [InlineData(0, 0, true, 19, "ACTIVITY follow")]
    [InlineData(0, 0, false, 20, "ACTIVITY inspect")]
    [InlineData(0, 40, true, 100, "ACTIVITY · following · 40 below")]
    [InlineData(12, 40, false, 43, "ACTIVITY · inspecting · 12 above · 40 below")]
    [InlineData(12, 40, true, 42, "ACTIVITY · following · 12 above · 40 below")]
    [InlineData(12, 40, false, 42, "ACTIVITY inspect ^12 v40")]
    [InlineData(12, 40, false, 24, "ACTIVITY inspect ^12 v40")]
    [InlineData(12, 40, false, 23, "ACTIVI… inspect ^12 v40")]
    [InlineData(12, 40, true, 10, "A… follow ^12 v40")]
    [InlineData(12, 40, true, 0, "ACTIVITY · following · 12 above · 40 below")]
    [InlineData(12, 40, true, -4, "ACTIVITY · following · 12 above · 40 below")]
    public void Title_ModeAndColumns_UsesExactLongShortOrCutForm(
        int above, int below, bool following, int columns, string expected)
    {
        var title = OwnerConsoleActivityTitle.Title(new(above, below), following, columns);
        Assert.Equal(expected, title);
        AssertTitleInvariants(title, following, columns);
    }

    [Fact]
    public void Title_AllOverflowModesAndWidths_PreservesModeCellsAndCharacterSet()
    {
        foreach (var above in new[] { 0, 1, 12 })
        foreach (var below in new[] { 0, 40 })
        foreach (var following in new[] { true, false })
        for (var columns = 1; columns <= 60; columns++)
        {
            var title = OwnerConsoleActivityTitle.Title(new(above, below), following, columns);
            AssertTitleInvariants(title, following, columns);
        }
    }

    private static void AssertTitleInvariants(string title, bool following, int columns)
    {
        Assert.Contains(following ? "follow" : "inspect", title);
        if (columns > 0 && !title.Contains('…')) Assert.True(title.GetColumns() <= columns);
        Assert.All(title, character =>
            Assert.True(character is >= '\u0020' and <= '\u007e' or '\u00b7' or '\u2026'));
    }
}
