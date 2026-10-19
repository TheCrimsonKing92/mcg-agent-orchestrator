using System.Data;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Scene = OwnerConsoleActivityOutcomeTests.Scene;

// Parallel-safe: instance apps remain uninitialized; scene stores and harness clocks are local.
public sealed class OwnerConsolePaneOverflowScenarioTests
{
    [Fact]
    public async Task MixedPaging_EachPane_UpdatesCountsWithoutChangingSelectionSequence()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene));
        SetFrames(scene, 5);
        Assert.True(scene.View.ActivityLines.Count > 5);
        var keys = new[] { Key.End, Key.Home, Key.CursorDown, Key.PageDown, Key.CursorUp, Key.PageUp, Key.End, Key.Home };
        for (var pane = 0; pane < 3; pane++)
        {
            var count = pane == 2 ? scene.View.ActivityLines.Count : 20;
            var expected = new[] { count - 1, 0, 1, 6, 5, 0, count - 1, 0 };
            var name = BaseTitle((OwnerConsolePane)pane);
            for (var index = 0; index < keys.Length; index++)
            {
                await scene.View.HandleKeyAsync(new Key(keys[index]));
                Assert.Equal(expected[index], Selection(scene, pane));
                Assert.Equal((OwnerConsolePane)pane, scene.View.FocusedPane);
                if (index is 0 or 6)
                    Assert.Equal(pane == 2 ? $"ACTIVITY · inspecting · {count - 5} above" : $"{name} · {count - 5} above", scene.View.PaneTitle((OwnerConsolePane)pane));
                if (index is 1 or 7)
                    Assert.Equal(pane == 2 ? $"ACTIVITY · following · {count - 5} below" : $"{name} · {count - 5} below", scene.View.PaneTitle((OwnerConsolePane)pane));
                if (index == 3)
                {
                    var overflow = OwnerConsoleOverflow.Of(count, Offset(scene, pane), 5);
                    Assert.True(overflow.Above > 0 && overflow.Below > 0);
                    Assert.Equal(pane == 2 ? OwnerConsoleActivityTitle.Title(overflow, false, 196) : OwnerConsoleOverflow.Title(name, overflow, 196), scene.View.PaneTitle((OwnerConsolePane)pane));
                }
            }
            await scene.View.HandleKeyAsync(Key.Tab);
        }
    }

    [Fact]
    public async Task DirectViewportAndRefresh_PreserveDecisionIdentityAndUpdateCounts()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene));
        SetFrames(scene, 5);
        await scene.View.HandleKeyAsync(Key.PageDown);
        var selected = scene.Controller.SelectedDecisionId;
        var viewport = scene.View.DecisionsPane.Viewport;
        scene.View.DecisionsPane.Viewport = new Rectangle(viewport.X, 3, viewport.Width, viewport.Height);
        Assert.Equal(selected, scene.Controller.SelectedDecisionId);
        Assert.Equal("DECISIONS · 3 above · 12 below", scene.View.PaneTitle(OwnerConsolePane.Decisions));

        scene.Harness.Questions.Items.Insert(0, new("new", "00000000", OwnerQuestionKind.HumanInput, "New question"));
        await scene.Render(Events(scene));
        Assert.Equal(selected, scene.Controller.SelectedDecisionId);
        Assert.Equal(21, scene.Controller.Model!.Decisions.Length);
        Assert.Equal(OwnerConsoleOverflow.Title("DECISIONS",
            OwnerConsoleOverflow.Of(21, scene.View.DecisionsPane.Viewport.Y, 5), 196),
            scene.View.PaneTitle(OwnerConsolePane.Decisions));
    }

    [Fact]
    public async Task ResizeAndNarrowFrame_RecomputeTitlesFromCurrentGeometry()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene));
        SetFrames(scene, 5);
        Assert.True(scene.View.ActivityLines.Count > 5);
        for (var pane = 0; pane < 3; pane++)
        {
            await scene.View.HandleKeyAsync(Key.PageDown);
            await scene.View.HandleKeyAsync(Key.Tab);
        }
        SetFrames(scene, 10);
        for (var pane = 0; pane < 3; pane++)
        {
            var count = pane == 2 ? scene.View.ActivityLines.Count : 20;
            Assert.Equal(pane == 2 ? OwnerConsoleActivityTitle.Title(OwnerConsoleOverflow.Of(count, Offset(scene, pane), 10), false, 196) : OwnerConsoleOverflow.Title(BaseTitle((OwnerConsolePane)pane),
                OwnerConsoleOverflow.Of(count, Offset(scene, pane), 10), 196),
                scene.View.PaneTitle((OwnerConsolePane)pane));
        }
        SetFrames(scene, 60);
        AssertBareTitles(scene);

        var frame = Assert.IsType<FrameView>(scene.View.DecisionsPane.SuperView);
        frame.Frame = new Rectangle(0, 0, 20, 7);
        scene.View.DecisionsPane.Frame = new Rectangle(0, 0, 18, 5);
        await scene.View.HandleKeyAsync(Key.End);
        Assert.Equal("DECISIONS ^15", scene.View.PaneTitle(OwnerConsolePane.Decisions));
    }

    [Theory]
    [InlineData("fitting")]
    [InlineData("empty")]
    [InlineData("zero-height")]
    [InlineData("before-layout")]
    [InlineData("loading")]
    [InlineData("unavailable")]
    public async Task NonOverflowStates_KeepBareTitlesAndBaselineContent(string state)
    {
        using var scene = new Scene();
        using var baseline = new Scene();
        await Arrange(scene, state);
        await Arrange(baseline, state);
        AssertBareTitles(scene);
        AssertContent(baseline, scene);
        for (var pane = 0; pane < 3; pane++)
        {
            foreach (var key in new[] { Key.PageDown, Key.End, Key.Home })
            {
                await scene.View.HandleKeyAsync(new Key(key));
                AssertBareTitles(scene);
                AssertContent(baseline, scene);
            }
            await scene.View.HandleKeyAsync(Key.Tab);
        }
    }

    private static async Task Arrange(Scene scene, string state)
    {
        if (state is "fitting" or "zero-height") Populate(scene, 3);
        if (state == "before-layout") Populate(scene, 20);
        await scene.Render([]);
        if (state != "before-layout") SetFrames(scene, state == "zero-height" ? 0 : 5);
        if (state is "loading" or "unavailable")
        {
            var loading = state == "loading";
            scene.View.Render(scene.Controller.Model! with
            {
                DecisionsState = new(Loading: loading, Error: loading ? null : "questions unavailable"),
                ActivityState = new(Loading: loading, Error: loading ? null : "log unavailable")
            });
            Assert.Equal(loading ? "loading..." : "ACTIVITY unavailable: log unavailable", Assert.Single(scene.View.ActivityLines));
            Assert.Equal(loading ? "loading..." : "DECISIONS unavailable: questions unavailable", Assert.Single(scene.View.DecisionLines));
        }
    }

    private static void AssertContent(Scene expected, Scene actual)
    {
        Assert.Equal(expected.View.DecisionLines, actual.View.DecisionLines);
        Assert.Equal(expected.View.ActivityLines, actual.View.ActivityLines);
        Assert.Equal(expected.View.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName),
            actual.View.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(expected.View.BoardTable.Rows.Count, actual.View.BoardTable.Rows.Count);
        Assert.Equal(expected.View.BoardTable.Rows.Cast<DataRow>().SelectMany(row => row.ItemArray),
            actual.View.BoardTable.Rows.Cast<DataRow>().SelectMany(row => row.ItemArray));
    }

    private static void AssertBareTitles(Scene scene)
    {
        Assert.Equal("DECISIONS", scene.View.PaneTitle(OwnerConsolePane.Decisions));
        Assert.Equal("BOARD", scene.View.PaneTitle(OwnerConsolePane.Board));
        Assert.Equal(scene.View.ActivityFollowing ? "ACTIVITY · following" : "ACTIVITY · inspecting", scene.View.PaneTitle(OwnerConsolePane.Activity));
    }

    private static string BaseTitle(OwnerConsolePane pane) => pane.ToString().ToUpperInvariant();

    private static int Offset(Scene scene, int pane) => pane == 0 ? scene.View.DecisionsPane.Viewport.Y :
        pane == 1 ? scene.View.BoardPane.RowOffset : scene.View.ActivityPane.Viewport.Y;

    private static int Selection(Scene scene, int pane) => pane == 0 ? scene.Controller.SelectedIndex :
        pane == 1 ? scene.View.SelectedGoalId is null ? -1 : scene.View.BoardPane.Value?.SelectedCell.Y ?? -1 :
        scene.View.ActivityPane.SelectedItem ?? -1;

    private static OwnerConductEvent[] Events(Scene scene)
    {
        var time = scene.Harness.Clock.GetUtcNow();
        return Enumerable.Range(0, 30).Select(index =>
            new OwnerConductEvent(time.AddSeconds(index), "acceptance", "00000000", "result=passed")).ToArray();
    }

    private static void Populate(Scene scene, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var id = index.ToString("D8");
            scene.Harness.AddGoal(id, "Goal " + index, AgentRole.Developer);
            scene.Harness.Questions.Items.Add(new("q" + index, id, OwnerQuestionKind.HumanInput, "Question " + index));
        }
    }

    private static void SetFrames(Scene scene, int height)
    {
        foreach (var pane in new[] { scene.View.DecisionsPane, scene.View.ActivityPane })
        {
            Assert.IsType<FrameView>(pane.SuperView).Frame = new Rectangle(0, 0, 200, height + 2);
            pane.Frame = new Rectangle(0, 0, 200, height);
        }
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        Assert.IsType<FrameView>(scene.View.BoardPane.SuperView).Frame = new Rectangle(0, 0, 200, height + headers + 2);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, height + headers);
    }
}
