using System.Data;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;
using Scene = OwnerConsoleActivityOutcomeTests.Scene;

// Parallel-safe: each scene owns an uninitialized application, fake I/O and harness clock.
public sealed class OwnerConsoleActivityFilterScenarioTests
{
    [Fact]
    public async Task Slash_ApplyEnterAndClear_MatchesNeverFilteredScene()
    {
        using var scene = new Scene();
        using var baseline = new Scene();
        await Populate(scene);
        await Populate(baseline);
        Assert.Equal(OwnerConsolePane.Decisions, scene.View.FocusedPane);
        Assert.Equal(6, scene.Controller.Model!.Activity.Length);

        var slash = new Key('/');
        await scene.View.HandleKeyAsync(slash);
        Assert.True(slash.Handled);
        Assert.True(scene.View.CommandLine.HasFocus);
        Assert.Equal("filter ", scene.View.CommandLine.Text);
        Assert.Equal(scene.View.CommandLine.Text.Length, scene.View.CommandLine.InsertionPoint);
        Assert.Equal(OwnerConsoleKeyHints.CommandLineHint, scene.View.HintText);
        scene.View.CommandLine.Text = "filter 2222";
        await scene.View.HandleKeyAsync(Key.Enter);

        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
        Assert.Equal("2222", scene.View.ActivityFilter);
        var matching = scene.Controller.Model.Activity.Where(item => item.GoalPrefix == "22222222").ToArray();
        Assert.Equal(2, matching.Length);
        Assert.Equal(matching.Select(OwnerActivityNarrator.Line), scene.View.ActivityLines);
        Assert.Contains("ACTIVITY filter \"2222\": 2 of 6 rows", scene.View.NoticeText);
        Assert.StartsWith("ACTIVITY · filter \"2222\" · following", scene.View.PaneTitle(OwnerConsolePane.Activity));
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.StartsWith("What happened: " + OwnerActivityNarrator.Line(matching[0]) + "\n",
            Assert.Single(scene.Dialogs.Messages).Text);

        await Command(scene, ":filter");
        await FocusActivity(baseline);
        Assert.Null(scene.View.ActivityFilter);
        Assert.Contains("ACTIVITY filter cleared", scene.View.NoticeText);
        EqualPanes(baseline, scene);
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("11111111-0000-0000")]
    public async Task TypedFilter_RoleAndLongId_ShowMatchingItemsAndCounts(string filter)
    {
        using var scene = new Scene();
        await Populate(scene);
        var expectedItems = scene.Controller.Model!.Activity.Where(item => filter == "developer"
            ? OwnerActivityNarrator.Line(item).Contains("Developer", StringComparison.OrdinalIgnoreCase)
            : item.GoalPrefix == "11111111").ToArray();
        Assert.Equal(2, expectedItems.Length);

        await Command(scene, "filter " + filter);

        Assert.Equal(ExpectedLines(expectedItems), scene.View.ActivityLines);
        Assert.Contains($"ACTIVITY filter \"{filter}\": {expectedItems.Length} of 6 rows", scene.View.NoticeText);
    }

    [Fact]
    public async Task Following_NewMatchingEventSelectsLatest_NonmatchKeepsPaneEqual()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Command(scene, "filter 2222");
        OwnerConductEvent[] matching = [.. Events(), Event(18, 14, "22222222")];

        await scene.Render(matching);

        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal("2222", scene.View.ActivityFilter);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(OwnerActivityNarrator.Line(scene.Controller.Model!.Activity[0]), SelectedLine(scene));
        var lines = scene.View.ActivityLines.ToArray();
        var selected = scene.View.ActivityPane.SelectedItem;
        var top = scene.View.ActivityPane.Viewport.Y;
        await scene.Render([.. matching, Event(18, 15, "33333333")]);
        Assert.Equal(lines, scene.View.ActivityLines);
        Assert.Equal(selected, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(top, scene.View.ActivityPane.Viewport.Y);
        Assert.True(scene.View.ActivityFollowing);
    }

    [Fact]
    public async Task Inspecting_FilterAndClear_KeepShownItemAndMode()
    {
        using var scene = new Scene();
        await Populate(scene);
        await FocusActivity(scene);
        var olderMatch = scene.Controller.Model!.Activity.Last(item => item.GoalPrefix == "22222222");
        await SelectLine(scene, OwnerActivityNarrator.Line(olderMatch));
        Assert.False(scene.View.ActivityFollowing);

        await Command(scene, "filter 2222");

        Assert.Equal(OwnerActivityNarrator.Line(olderMatch), SelectedLine(scene));
        Assert.False(scene.View.ActivityFollowing);
        await Command(scene, "filter");
        Assert.Equal(OwnerActivityNarrator.Line(olderMatch), SelectedLine(scene));
        Assert.False(scene.View.ActivityFollowing);
        Assert.Null(scene.View.ActivityFilter);
    }

    [Fact]
    public async Task Inspecting_HiddenItemFallsBackToNewest_FollowAndHomeKeepFilter()
    {
        using var scene = new Scene();
        await Populate(scene);
        await FocusActivity(scene);
        var hidden = scene.Controller.Model!.Activity.First(item => item.GoalPrefix == "11111111");
        await SelectLine(scene, OwnerActivityNarrator.Line(hidden));
        Assert.False(scene.View.ActivityFollowing);

        await Command(scene, "filter 2222");

        Assert.False(scene.View.ActivityFollowing);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(OwnerActivityNarrator.Line(scene.Controller.Model.Activity.First(item => item.GoalPrefix == "22222222")),
            SelectedLine(scene));
        await Command(scene, "follow");
        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal("2222", scene.View.ActivityFilter);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.False(scene.View.ActivityFollowing);
        await scene.View.HandleKeyAsync(Key.Home);
        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal("2222", scene.View.ActivityFilter);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
    }

    [Fact]
    public async Task EmptyFilterResult_EnterShowsFailureWithoutDialog()
    {
        using var scene = new Scene();
        await Populate(scene);

        await Command(scene, "filter zzzz");

        Assert.Equal(new[] { "No ACTIVITY row matches filter \"zzzz\"; type :filter to clear it." }, scene.View.ActivityLines);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Empty(scene.Dialogs.Messages);
        Assert.Contains("Enter: no ACTIVITY row matches filter \"zzzz\"; type :filter to clear it", scene.View.NoticeText);
    }

    [Fact]
    public async Task Slash_CancelAndOpenCommand_DoNotApplyFilter_SpacesClearIt()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Command(scene, "filter 2222");
        var lines = scene.View.ActivityLines.ToArray();

        await scene.View.HandleKeyAsync(new Key('/'));
        Assert.Equal("filter 2222", scene.View.CommandLine.Text);
        Assert.Equal(scene.View.CommandLine.Text.Length, scene.View.CommandLine.InsertionPoint);
        scene.View.CommandLine.Text = "filter zzzz";
        await scene.View.HandleKeyAsync(Key.Esc);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
        Assert.Equal("2222", scene.View.ActivityFilter);
        Assert.Equal(lines, scene.View.ActivityLines);

        await scene.View.HandleKeyAsync(new Key(':'));
        scene.View.CommandLine.Text = "filter 1111";
        var slash = new Key('/');
        await scene.View.HandleKeyAsync(slash);
        Assert.False(slash.Handled);
        Assert.Equal("filter 1111", scene.View.CommandLine.Text);
        await scene.View.HandleKeyAsync(Key.Esc);
        await Command(scene, "filter     ");
        Assert.Null(scene.View.ActivityFilter);
        Assert.Contains("ACTIVITY filter cleared", scene.View.NoticeText);
        Assert.Equal(ExpectedLines(scene.Controller.Model!.Activity), scene.View.ActivityLines);
    }

    [Theory]
    [InlineData("following")]
    [InlineData("inspecting")]
    [InlineData("single-date")]
    [InlineData("loading")]
    public async Task ClearFilter_AllBaselineControls_KeepActivityAndOtherPanesEqual(string mode)
    {
        using var scene = new Scene();
        using var baseline = new Scene();
        foreach (var current in new[] { scene, baseline })
        {
            await Populate(current, singleDate: mode == "single-date");
            current.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 20);
            await FocusActivity(current);
            if (mode == "inspecting")
            {
                var item = current.Controller.Model!.Activity.First(item => item.GoalPrefix == "22222222");
                await SelectLine(current, OwnerActivityNarrator.Line(item));
                Assert.False(current.View.ActivityFollowing);
                Assert.Equal(0, current.View.ActivityPane.Viewport.Y);
            }
            if (mode == "loading")
                current.View.Render(current.Controller.Model! with { ActivityState = new(Loading: true) });
        }

        await Command(scene, "filter 2222");
        if (mode == "loading") Assert.Equal(new[] { "loading..." }, scene.View.ActivityLines);
        await Command(scene, "filter");

        EqualPanes(baseline, scene);
        Assert.Null(scene.View.ActivityFilter);
        Assert.Contains("ACTIVITY filter cleared", scene.View.NoticeText);
    }

    private static async Task Populate(Scene scene, bool singleDate = false)
    {
        scene.AddGoals();
        await scene.Render(Events(singleDate));
        Assert.Equal(6, scene.Controller.Model!.Activity.Length);
    }

    private static OwnerConductEvent[] Events(bool singleDate = false) =>
    [
        Event(singleDate ? 18 : 17, 9, "11111111", developer: true),
        Event(singleDate ? 18 : 17, 10, "33333333"),
        Event(18, 9, "11111111"),
        Event(18, 10, "33333333", developer: true),
        Event(18, 12, "22222222"),
        Event(18, 13, "22222222")
    ];

    private static OwnerConductEvent Event(int day, int hour, string goal, bool developer = false)
    {
        var local = new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
            developer ? "goal-lifecycle" : "acceptance", goal,
            developer ? "TaskDispatched role=Developer" : "result=passed");
    }

    private static string[] ExpectedLines(IEnumerable<OwnerConsoleActivityItem> source)
    {
        var items = source.ToArray();
        var dates = items.Select(item => DateOnly.FromDateTime(item.Timestamp.ToLocalTime().DateTime)).ToArray();
        var rows = new List<string>();
        for (var index = 0; index < items.Length; index++)
        {
            if (dates.Distinct().Count() > 1 && (index == 0 || dates[index] != dates[index - 1]))
                rows.Add(OwnerConsoleActivityRows.Separator(dates[index]));
            rows.Add(OwnerActivityNarrator.Line(items[index]));
        }
        return rows.ToArray();
    }

    private static async Task FocusActivity(Scene scene)
    {
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
    }

    private static async Task Command(Scene scene, string text)
    {
        await scene.View.HandleKeyAsync(new Key(':'));
        scene.View.CommandLine.Text = text;
        await scene.View.HandleKeyAsync(Key.Enter);
    }

    private static async Task SelectLine(Scene scene, string line)
    {
        var target = Array.IndexOf(scene.View.ActivityLines.ToArray(), line);
        Assert.True(target > 0, "The inspected fixture item must be below the initial selection.");
        for (var index = 0; index < target; index++) await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(line, SelectedLine(scene));
    }

    private static string SelectedLine(Scene scene) => scene.View.ActivityLines[scene.View.ActivityPane.SelectedItem!.Value];

    private static void EqualPanes(Scene expected, Scene actual)
    {
        Assert.Equal(expected.View.ActivityLines, actual.View.ActivityLines);
        Assert.Equal(expected.View.ActivityPane.SelectedItem, actual.View.ActivityPane.SelectedItem);
        Assert.Equal(expected.View.ActivityPane.Viewport.Y, actual.View.ActivityPane.Viewport.Y);
        Assert.Equal(expected.View.PaneTitle(OwnerConsolePane.Activity), actual.View.PaneTitle(OwnerConsolePane.Activity));
        Assert.Equal(expected.View.ActivityFollowing, actual.View.ActivityFollowing);
        Assert.Equal(expected.View.DecisionLines, actual.View.DecisionLines);
        Assert.Equal(expected.View.BoardTable.Rows.Count, actual.View.BoardTable.Rows.Count);
        for (var index = 0; index < expected.View.BoardTable.Rows.Count; index++)
            Assert.Equal(expected.View.BoardTable.Rows[index].ItemArray, actual.View.BoardTable.Rows[index].ItemArray);
    }
}
