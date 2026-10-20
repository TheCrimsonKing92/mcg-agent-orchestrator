using System.Data;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Scene = OwnerConsoleActivityOutcomeTests.Scene;

// Parallel-safe: each scene owns its uninitialized application, fake I/O, and fixture clock.
public sealed class OwnerConsoleActivityFollowScenarioTests
{
    [Fact]
    public async Task Refresh_OwnerMovesThenHome_FollowsInspectsAndResumes()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        AssertFollowing(scene);
        await scene.Render(Events(scene, 33));
        AssertFollowing(scene);
        Assert.Equal(OwnerActivityNarrator.Line(scene.Controller.Model!.Activity[0]), scene.View.ActivityLines[0]);
        Assert.Equal(scene.Harness.Clock.GetUtcNow().AddSeconds(32), scene.Controller.Model.Activity[0].Timestamp);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        var line = SelectedLine(scene);
        var top = scene.View.ActivityPane.Viewport.Y;
        AssertInspecting(scene);
        await scene.Render(Events(scene, 35));
        Assert.Equal(4, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(line, scene.View.ActivityLines[4]);
        Assert.Equal(top + 2, scene.View.ActivityPane.Viewport.Y);
        await scene.View.HandleKeyAsync(Key.Home);
        AssertFollowing(scene);
        await scene.Render(Events(scene, 36));
        AssertFollowing(scene);
        Assert.Equal(scene.Harness.Clock.GetUtcNow().AddSeconds(35), scene.Controller.Model!.Activity[0].Timestamp);
        Assert.Equal(OwnerActivityNarrator.Line(scene.Controller.Model.Activity[0]), SelectedLine(scene));
    }

    [Fact]
    public async Task FollowCommand_FromDecisions_MatchesHomeInActivity()
    {
        using var typed = new Scene();
        using var home = new Scene();
        foreach (var scene in new[] { typed, home })
        {
            await Populate(scene);
            await Activity(scene);
            scene.View.ActivityPane.SelectedItem = 2;
            await scene.Render(Events(scene, 31));
            Assert.Equal(3, scene.View.ActivityPane.SelectedItem);
            AssertInspecting(scene);
        }
        await typed.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Decisions, typed.View.FocusedPane);
        await Command(typed, "follow");
        await home.View.HandleKeyAsync(Key.Home);
        AssertFollowing(typed);
        Assert.Equal(home.View.FocusedPane, typed.View.FocusedPane);
        Assert.Equal(home.View.ActivityFollowing, typed.View.ActivityFollowing);
        Assert.Equal(home.View.ActivityPane.SelectedItem, typed.View.ActivityPane.SelectedItem);
        Assert.Equal(home.View.ActivityPane.Viewport, typed.View.ActivityPane.Viewport);
        Assert.Equal(home.View.HintText, typed.View.HintText);
        Assert.Same(OwnerConsoleBindings.Follow, OwnerConsoleBindings.ForLine("follow"));
    }

    [Fact]
    public async Task ViewportScroll_SelectionUnchanged_AnchorsTheVisibleEvent()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        scene.View.ActivityPane.Viewport = scene.View.ActivityPane.Viewport with { Y = 3 };
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(3, scene.View.ActivityPane.Viewport.Y);
        AssertInspecting(scene);
        var topLine = scene.View.ActivityLines[3];
        await scene.Render(Events(scene, 32));
        Assert.Equal(2, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(5, scene.View.ActivityPane.Viewport.Y);
        Assert.Equal(topLine, scene.View.ActivityLines[scene.View.ActivityPane.Viewport.Y]);
    }

    [Fact]
    public async Task SelectionSet_DirectOwnerMove_KeepsTheEventAfterAppend()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        scene.View.ActivityPane.SelectedItem = 2;
        var line = SelectedLine(scene);
        AssertInspecting(scene);
        await scene.Render(Events(scene, 31));
        Assert.Equal(3, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(line, SelectedLine(scene));
    }

    [Fact]
    public async Task Navigation_SelectionCannotMove_StaysFollowing()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        await scene.View.HandleKeyAsync(Key.CursorUp);
        AssertFollowing(scene);
        await scene.Render(Events(scene, 1));
        Assert.Single(scene.View.ActivityLines);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        AssertFollowing(scene);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resize_WidthChanges_KeepsModeAndSelectedEventVisible(bool following)
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        if (!following) await scene.View.HandleKeyAsync(Key.PageDown);
        var item = SelectedItem(scene);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 80, 5);
        Assert.Equal(following, scene.View.ActivityFollowing);
        Assert.Equal(item, SelectedItem(scene));
        var pane = scene.View.ActivityPane;
        Assert.InRange(pane.SelectedItem!.Value, pane.Viewport.Y, pane.Viewport.Y + pane.Viewport.Height - 1);
        if (following) AssertFollowing(scene);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OtherSurfaces_OpenAndClose_KeepActivityModeAndEvent(bool following)
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        if (!following) scene.View.ActivityPane.SelectedItem = 2;
        var item = SelectedItem(scene);
        void Unchanged()
        {
            Assert.Equal(following, scene.View.ActivityFollowing);
            Assert.Equal(item, SelectedItem(scene));
        }
        for (var tab = 0; tab < 3; tab++) { await scene.View.HandleKeyAsync(Key.Tab); Unchanged(); }
        await scene.View.HandleKeyAsync(new Key(':'));
        Assert.Contains("Enter run  Esc cancel", scene.View.HintText);
        Unchanged();
        await scene.View.HandleKeyAsync(Key.Esc);
        Unchanged();
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Contains(OwnerActivityNarrator.Line(item), Assert.Single(scene.Dialogs.Messages).Text);
        Unchanged();
        await Command(scene, "epics");
        Assert.True(scene.View.EpicView.IsOpen);
        Unchanged();
        await scene.View.HandleKeyAsync(Key.Esc);
        Assert.False(scene.View.EpicView.IsOpen);
        Unchanged();
        await Command(scene, "gate");
        Assert.True(scene.View.GateView.IsOpen);
        Unchanged();
        await scene.View.HandleKeyAsync(Key.Esc);
        Assert.False(scene.View.GateView.IsOpen);
        Unchanged();
        await scene.Render(Events(scene, 31));
        Assert.Equal(following, scene.View.ActivityFollowing);
        if (following) AssertFollowing(scene);
        else Assert.Equal(OwnerConsoleActivityFollow.Key([item], 0),
            OwnerConsoleActivityFollow.Key([SelectedItem(scene)], 0));
    }

    [Fact]
    public async Task LoadingAndError_ItemsReturn_ReselectsRememberedEvent()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        var expected = scene.Controller.Model!.Activity[7];
        scene.View.ActivityPane.SelectedItem = 7;
        Assert.Equal(7, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(OwnerConsoleActivityFollow.Key([expected], 0),
            OwnerConsoleActivityFollow.Key([SelectedItem(scene)], 0));
        AssertInspecting(scene);
        var model = scene.Controller.Model!;
        var item = SelectedItem(scene);
        scene.View.Render(model with { ActivityState = new(Loading: true) });
        Assert.Equal("loading...", Assert.Single(scene.View.ActivityLines));
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.False(scene.View.ActivityFollowing);
        scene.View.Render(model with { ActivityState = new(Error: "offline") });
        Assert.Equal("ACTIVITY unavailable: offline", Assert.Single(scene.View.ActivityLines));
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.False(scene.View.ActivityFollowing);
        scene.View.Render(model);
        Assert.Equal(7, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(item, SelectedItem(scene));
        AssertInspecting(scene);
    }

    [Fact]
    public async Task OldestEvent_AgesOut_SelectsLastRowAndKeepsInspecting()
    {
        using var scene = new Scene();
        await Populate(scene, OwnerConsoleViewModelBuilder.MaxActivityItems);
        await Activity(scene);
        await scene.View.HandleKeyAsync(Key.End);
        Assert.Equal(OwnerConsoleViewModelBuilder.MaxActivityItems - 1, scene.View.ActivityPane.SelectedItem);
        AssertInspecting(scene);
        var previous = SelectedItem(scene);
        await scene.Render(Events(scene, OwnerConsoleViewModelBuilder.MaxActivityItems + 3));
        Assert.Equal(OwnerConsoleViewModelBuilder.MaxActivityItems - 1, scene.View.ActivityPane.SelectedItem);
        Assert.NotEqual(previous, SelectedItem(scene));
        AssertInspecting(scene);
    }

    [Fact]
    public async Task EmptyPane_Home_HandlesKeyAndKeepsFollowing()
    {
        using var scene = new Scene();
        await scene.Render([]);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        await Activity(scene);
        Assert.True(scene.View.ActivityFollowing);
        Assert.Null(scene.View.ActivityPane.SelectedItem);
        var home = new Key(Key.Home);
        await scene.View.HandleKeyAsync(home);
        Assert.True(home.Handled);
        Assert.True(scene.View.ActivityFollowing);
        Assert.Null(scene.View.ActivityPane.SelectedItem);
        Assert.Equal(0, scene.View.ActivityPane.Viewport.Y);
        Assert.StartsWith("Following latest", scene.View.HintText);
    }

    [Fact]
    public async Task DuplicateEvents_Append_KeepsTheOlderOccurrence()
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        var model = scene.Controller.Model!;
        var duplicate = model.Activity[2];
        scene.View.Render(model with { Activity = [model.Activity[0], duplicate, duplicate] });
        scene.View.ActivityPane.SelectedItem = 2;
        scene.View.Render(model with { Activity = [model.Activity[0] with { Detail = "new" }, model.Activity[0], duplicate, duplicate] });
        Assert.Equal(3, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(duplicate, SelectedItem(scene));
        AssertInspecting(scene);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdenticalRefresh_EitherMode_PreservesPresentation(bool following)
    {
        using var scene = new Scene();
        await Populate(scene);
        await Activity(scene);
        if (!following) await scene.View.HandleKeyAsync(Key.PageDown);
        var lines = scene.View.ActivityLines.ToArray();
        var selected = scene.View.ActivityPane.SelectedItem;
        var viewport = scene.View.ActivityPane.Viewport;
        var hint = scene.View.HintText;
        scene.View.Render(scene.Controller.Model!);
        Assert.Equal(lines, scene.View.ActivityLines);
        Assert.Equal(selected, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(viewport, scene.View.ActivityPane.Viewport);
        Assert.Equal(hint, scene.View.HintText);
    }

    [Fact]
    public async Task ActivityMode_AppendsAndOtherPaneKeys_LeaveOtherPanesEqual()
    {
        using var following = new Scene();
        using var inspecting = new Scene();
        foreach (var scene in new[] { following, inspecting })
        {
            await Populate(scene, otherPanes: true);
            await Activity(scene);
        }
        inspecting.View.ActivityPane.SelectedItem = 2;
        Assert.True(following.View.ActivityFollowing);
        Assert.False(inspecting.View.ActivityFollowing);
        for (var count = 31; count <= 33; count++)
        {
            await following.Render(Events(following, count));
            await inspecting.Render(Events(inspecting, count));
            EqualOtherPanes(following, inspecting);
        }
        foreach (var pane in new[] { OwnerConsolePane.Decisions, OwnerConsolePane.Board })
        {
            await following.View.HandleKeyAsync(Key.Tab);
            await inspecting.View.HandleKeyAsync(Key.Tab);
            Assert.Equal(pane, following.View.FocusedPane);
            const string suffix = "  Home/End first/last  PgUp/PgDn page  Tab/Shift+Tab pane  e epics  / filter  l landings  g gate  : command  ? help  q quit";
            var hint = (pane == OwnerConsolePane.Decisions ? "Enter detail  a accept default  r answer" : "Enter goal detail") + suffix;
            foreach (var key in new[] { Key.End, Key.Home, Key.CursorDown, Key.PageDown, Key.CursorUp, Key.PageUp })
            {
                await following.View.HandleKeyAsync(key);
                await inspecting.View.HandleKeyAsync(key);
                EqualOtherPanes(following, inspecting);
                Assert.Equal(hint, following.View.HintText);
                Assert.Equal(hint, inspecting.View.HintText);
            }
        }
    }

    [Fact]
    public async Task Refresh_ControlledSecondBoundary_PreservesModesAndOtherPanes()
    {
        // Separate the displayed hour from a clock-omitting view without waiting on real time.
        var systemLocal = TimeProvider.System.GetLocalNow();
        var before = new DateTimeOffset(2026, 1, 1, (systemLocal.Hour + 6) % 24, 12, 59, 900, TimeSpan.Zero);
        var timerChanges = new List<TimeSpan>();
        var followingClock = new OwnerConsoleTestClock { Now = before, Zone = TimeZoneInfo.Utc, TimerChanged = timerChanges.Add };
        var inspectingClock = new OwnerConsoleTestClock { Now = before, Zone = TimeZoneInfo.Utc, TimerChanged = timerChanges.Add };
        using var following = new Scene(clock: followingClock);
        using var inspecting = new Scene(clock: inspectingClock);
        var scenes = new[] { following, inspecting };
        foreach (var scene in scenes)
        {
            Assert.False(scene.App.Initialized);
            await Populate(scene, otherPanes: true);
            await Activity(scene);
        }

        // Reuse identical inputs throughout: only the render clock crosses the boundary.
        var model = following.Controller.Model!;
        // Scene.Render supplies no LastConductEvent, independently of its activity rows.
        Assert.Null(model.Status.LastEventAge);
        foreach (var scene in scenes) scene.View.Render(model);
        await inspecting.View.HandleKeyAsync(Key.PageDown);
        AssertFollowing(following);
        AssertInspecting(inspecting);
        var presentations = scenes.Select(scene => new
        {
            Scene = scene,
            Lines = scene.View.ActivityLines.ToArray(),
            Selected = scene.View.ActivityPane.SelectedItem,
            Viewport = scene.View.ActivityPane.Viewport,
            Hint = scene.View.HintText
        }).ToArray();

        foreach (var instant in new[] { before, before.AddMilliseconds(200) })
        {
            followingClock.Now = inspectingClock.Now = instant;
            foreach (var presentation in presentations)
            {
                var scene = presentation.Scene;
                scene.View.Render(model);
                Assert.Equal($"conductor: running | active: 12 | decisions: 12 | last event: unknown | landed today: 0 | bell: on | refreshed {instant:HH:mm:ss}",
                    scene.View.StatusText);
                Assert.Equal(presentation.Lines, scene.View.ActivityLines);
                Assert.Equal(presentation.Selected, scene.View.ActivityPane.SelectedItem);
                Assert.Equal(presentation.Viewport, scene.View.ActivityPane.Viewport);
                Assert.Equal(presentation.Hint, scene.View.HintText);
                Assert.False(scene.App.Initialized);
            }
            AssertFollowing(following);
            AssertInspecting(inspecting);
            EqualOtherPanes(following, inspecting);
            Assert.Empty(timerChanges);
        }
    }

    [Fact]
    public void Registry_Follow_IsKeylessGlobalViewCommandAfterFleet()
    {
        Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));
        var rows = OwnerConsoleBindings.All.ToArray();
        var follow = OwnerConsoleBindings.Follow;
        Assert.Same(follow, rows[Array.FindIndex(rows, row => row.Command == "fleet") + 1]);
        Assert.Equal(OwnerConsoleScope.Global, follow.Scope);
        Assert.Equal(OwnerConsoleCommandOwner.View, follow.Owner);
        Assert.Null(follow.Key);
        Assert.Equal(new[] { 'a', 'e', 'g', 'l', 'q', 'r' }, OwnerConsoleBindings.Commands
            .Where(row => row.Key is { } key && char.IsLetter(key)).Select(row => row.Key!.Value).Distinct().Order());
        var help = Assert.Single(OwnerConsoleKeyHints.HelpText.Split('\n'), line => line.StartsWith(":follow: ", StringComparison.Ordinal));
        Assert.Contains("Home in ACTIVITY", help);
    }

    private static async Task Populate(Scene scene, int count = 30, bool otherPanes = false)
    {
        for (var index = 0; index < (otherPanes ? 12 : 1); index++)
        {
            var id = index.ToString("D8");
            scene.Harness.AddGoal(id, "Goal " + index, AgentRole.Developer);
            if (otherPanes) scene.Harness.Questions.Items.Add(new("q" + index, id, OwnerQuestionKind.HumanInput, "Question " + index));
        }
        await scene.Render(Events(scene, count));
        Assert.Equal(count + (otherPanes ? 12 : 0), scene.View.ActivityLines.Count);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.DecisionsPane.Frame = new Rectangle(0, 0, 200, 5);
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 5 + headers);
    }

    private static OwnerConductEvent[] Events(Scene scene, int count) => Enumerable.Range(0, count)
        .Select(index => new OwnerConductEvent(scene.Harness.Clock.GetUtcNow().AddSeconds(index), "acceptance", "00000000", "result=passed")).ToArray();
    private static async Task Activity(Scene scene)
    {
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
    }
    private static async Task Command(Scene scene, string command)
    {
        await scene.View.HandleKeyAsync(new Key(':'));
        scene.View.CommandLine.Text = command;
        await scene.View.HandleKeyAsync(Key.Enter);
    }
    private static string SelectedLine(Scene scene) => scene.View.ActivityLines[scene.View.ActivityPane.SelectedItem!.Value];
    private static OwnerConsoleActivityItem SelectedItem(Scene scene) => scene.Controller.Model!.Activity[scene.View.ActivityPane.SelectedItem!.Value];
    private static void AssertFollowing(Scene scene)
    {
        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(0, scene.View.ActivityPane.Viewport.Y);
        Assert.StartsWith("Following latest", scene.View.HintText);
    }
    private static void AssertInspecting(Scene scene)
    {
        Assert.False(scene.View.ActivityFollowing);
        Assert.StartsWith("Inspecting  Home follow latest", scene.View.HintText);
    }
    private static void EqualOtherPanes(Scene first, Scene second)
    {
        Assert.Equal(first.Controller.SelectedIndex, second.Controller.SelectedIndex);
        Assert.Equal(first.View.BoardPane.Value!.SelectedCell, second.View.BoardPane.Value!.SelectedCell);
        Assert.Equal(first.View.BoardPane.RowOffset, second.View.BoardPane.RowOffset);
        Assert.Equal(first.View.DecisionLines, second.View.DecisionLines);
        Assert.Equal(first.View.BoardTable.Rows.Cast<DataRow>().Select(row => row.ItemArray),
            second.View.BoardTable.Rows.Cast<DataRow>().Select(row => row.ItemArray));
        Assert.Equal(first.View.StatusText, second.View.StatusText);
    }
}
