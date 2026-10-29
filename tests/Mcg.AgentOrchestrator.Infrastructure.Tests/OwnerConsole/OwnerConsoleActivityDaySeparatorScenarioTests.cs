using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;
using Scene = OwnerConsoleActivityOutcomeTests.Scene;

// Parallel-safe: each scene owns its uninitialized application, fake I/O and clock.
public sealed class OwnerConsoleActivityDaySeparatorScenarioTests
{
    [Fact]
    public async Task Enter_MixedLocalDates_RejectsSeparatorAndOpensMappedEvents()
    {
        using var scene = new Scene();
        scene.AddGoals();
        await scene.Render([Event(17, 23, 59, 30), Event(18, 0, 0, 30)]);
        var items = scene.Controller.Model!.Activity;
        Assert.Equal(2, items.Length);
        Assert.Equal(new[] { "-- Sun 2026-10-18 --", OwnerActivityNarrator.Line(items[0]),
            "-- Sat 2026-10-17 --", OwnerActivityNarrator.Line(items[1]) }, scene.View.ActivityLines);
        await FocusActivity(scene);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);

        await scene.View.HandleKeyAsync(Key.Enter);

        Assert.Empty(scene.Dialogs.Messages);
        Assert.Contains("Enter: a day separator has no detail; select an event row", scene.View.NoticeText);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(1, scene.View.ActivityPane.SelectedItem);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.StartsWith("What happened: " + OwnerActivityNarrator.Line(items[0]) + "\n",
            Assert.Single(scene.Dialogs.Messages).Text);

        await scene.View.HandleKeyAsync(Key.Home);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        for (var down = 0; down < 3; down++) await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(3, scene.View.ActivityPane.SelectedItem);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Equal(2, scene.Dialogs.Messages.Count);
        Assert.StartsWith("What happened: " + OwnerActivityNarrator.Line(items[1]) + "\n",
            scene.Dialogs.Messages[1].Text);
    }

    [Fact]
    public async Task Render_SingleLocalDate_MatchesExistingLinesDetailAndLoading()
    {
        using var scene = new Scene();
        scene.AddGoals();
        await scene.Render([Event(18, 12, 0, 0), Event(18, 12, 0, 1), Event(18, 12, 0, 2)]);
        var model = scene.Controller.Model!;
        Assert.Equal(3, model.Activity.Length);
        Assert.Equal(model.Activity.Select(item => OwnerConsoleLineFitter.Fit(
            OwnerActivityNarrator.Line(item), OwnerActivityNarrator.LineSpans(item), 0)), scene.View.ActivityLines);
        await FocusActivity(scene);

        await scene.View.HandleKeyAsync(Key.Enter);

        Assert.StartsWith("What happened: " + OwnerActivityNarrator.Line(model.Activity[0]) + "\n",
            Assert.Single(scene.Dialogs.Messages).Text);
        var lines = scene.View.ActivityLines.ToArray();
        scene.View.Render(model with { ActivityState = new(Loading: true) });
        Assert.Equal(lines, scene.View.ActivityLines);
        Assert.Contains("(refreshing)", scene.View.PaneTitle(OwnerConsolePane.Activity));
    }

    private static async Task FocusActivity(Scene scene)
    {
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
    }

    private static OwnerConductEvent Event(int day, int hour, int minute, int second)
    {
        var local = new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Unspecified);
        return new(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
            "acceptance", "11111111", "result=passed");
    }
}
