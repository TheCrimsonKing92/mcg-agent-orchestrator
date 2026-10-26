using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.ViewBase;

// Per-test views without application/driver/global configuration; safe in parallel.
public sealed class OwnerConsoleDialogWidthTests
{
    [Theory]
    [InlineData(80, 65)]
    [InlineData(120, 99)]
    [InlineData(200, 167)]
    public void TextColumnsMatchPercentShadowAndBorders(int screen, int expected) =>
        Assert.Equal(expected, OwnerConsoleDialogWidth.TextColumns(screen));

    [Theory]
    [InlineData(null, 60)]
    [InlineData(30, 40)]
    [InlineData(150, 150)]
    public void DiagramUsesFallbackAndFloor(int? available, int expected) =>
        Assert.Equal(expected, OwnerConsoleDialogWidth.ForDiagram(available));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownScreenWidthIsNull(int screen) => Assert.Null(OwnerConsoleDialogWidth.TextColumns(screen));

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void ColumnsFitRealDialogBody(int screen)
    {
        using var dialog = new OwnerConsoleTextDialog("Fleet", string.Join('\n', Enumerable.Repeat("lane", 200)));
        dialog.ShadowStyle = ShadowStyles.Transparent;
        dialog.Layout(new Size(screen, 40));
        Assert.True(dialog.Body.Viewport.Width > 0);
        Assert.Equal(OwnerConsoleDialogWidth.TextColumns(screen), dialog.Body.Viewport.Width);
    }
}
