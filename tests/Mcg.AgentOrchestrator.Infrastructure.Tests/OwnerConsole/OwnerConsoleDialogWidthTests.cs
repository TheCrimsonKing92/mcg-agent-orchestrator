using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

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

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void GoalActions_HiddenButtonsDoNotPushCloseOutsideDialog(int screen)
    {
        using var dialog = new OwnerConsoleTextDialog("Goal", "Active goal");
        var close = new Button { Text = "Close", IsDefault = true };
        dialog.AddButton(close);
        var actions = OwnerConsoleDialogBindings.GoalDetail.Where(row => row.Command != "quit")
            .ToDictionary(row => row.Key!.Value, row => new Button { Text = OwnerConsoleDialogBindings.Label(row) });
        foreach (var button in actions.Values) dialog.AddConditionalButton(button);
        foreach (var (key, button) in actions) button.Visible = key == 'e';

        AssertVisibleButtonsFit(screen);
        actions['d'].Visible = true;
        AssertVisibleButtonsFit(screen);
        actions['d'].Visible = false;
        AssertVisibleButtonsFit(80);
        Assert.Equal(actions.Count + 1, dialog.Buttons.Length);

        void AssertVisibleButtonsFit(int columns)
        {
            dialog.Layout(new Size(columns, 40));
            foreach (var button in dialog.Buttons.Where(button => button.Visible))
            {
                Assert.True(button.Frame.X >= 0, $"{button.Text} starts outside the dialog: {button.Frame}");
                Assert.True(button.Frame.Right <= button.SuperView!.Viewport.Width,
                    $"{button.Text} extends past the dialog: {button.Frame}");
            }
        }
    }
}
