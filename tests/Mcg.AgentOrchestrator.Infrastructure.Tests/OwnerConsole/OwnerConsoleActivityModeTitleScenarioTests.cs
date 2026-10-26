using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Scene = OwnerConsoleActivityOutcomeTests.Scene;

// Parallel-safe: instance apps remain uninitialized; scene stores and harness clocks are local.
public sealed class OwnerConsoleActivityModeTitleScenarioTests
{
    [Fact]
    public async Task ModeTitle_FocusCommandsAndRefresh_AlwaysMatchesActivityState()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene, 30));
        SetFrames(scene, 5);
        Assert.Equal(OwnerConsolePane.Decisions, scene.View.FocusedPane);
        Assert.True(scene.View.ActivityLines.Count > 5);
        AssertStep(scene);
        Assert.Equal($"ACTIVITY · following · {scene.View.ActivityLines.Count - 5} below",
            scene.View.PaneTitle(OwnerConsolePane.Activity));

        await Step(scene, Key.Tab);
        await Step(scene, Key.Tab);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
        await Step(scene, Key.CursorDown);
        Assert.False(scene.View.ActivityFollowing);
        await Step(scene, Key.Tab);
        Assert.Equal(OwnerConsolePane.Decisions, scene.View.FocusedPane);
        await Step(scene, Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
        Assert.Equal($"ACTIVITY · inspecting · {scene.View.ActivityLines.Count - 5} below",
            scene.View.PaneTitle(OwnerConsolePane.Activity));

        await Step(scene, new Key(':'), commandOpen: true);
        Assert.Equal(OwnerConsoleKeyHints.CommandLineHint, scene.View.HintText);
        await Step(scene, Key.Esc);
        await Step(scene, new Key(':'), commandOpen: true);
        scene.View.CommandLine.Text = "follow";
        AssertStep(scene, commandOpen: true);
        await Step(scene, Key.Enter);
        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal($"ACTIVITY · following · {scene.View.ActivityLines.Count - 5} below",
            scene.View.PaneTitle(OwnerConsolePane.Activity));

        await Step(scene, Key.End);
        await Step(scene, Key.Tab);
        Assert.Equal(OwnerConsolePane.Decisions, scene.View.FocusedPane);
        await scene.Render(Events(scene, 33));
        AssertStep(scene);
        await Step(scene, Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
    }

    [Fact]
    public async Task InspectingTitle_NarrowThenWideFrame_KeepsModeAndBothOverflowCounts()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene, 30));
        SetFrames(scene, 5);
        await Step(scene, Key.Tab);
        await Step(scene, Key.Tab);
        await Step(scene, Key.CursorDown);
        await Step(scene, Key.PageDown);
        Assert.False(scene.View.ActivityFollowing);
        Assert.True(ActivityOverflow(scene) is { Above: > 0, Below: > 0 });

        var frame = Assert.IsType<FrameView>(scene.View.ActivityPane.SuperView);
        frame.Frame = new Rectangle(0, 0, 27, 7);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 25, 5);
        Assert.False(scene.View.ActivityFollowing);
        Assert.True(ActivityOverflow(scene) is { Above: > 0, Below: > 0 });
        Assert.Equal(OwnerConsoleActivityTitle.Title(ActivityOverflow(scene), false, 23),
            scene.View.PaneTitle(OwnerConsolePane.Activity));
        Assert.Contains(" inspect ^", scene.View.PaneTitle(OwnerConsolePane.Activity));

        frame.Frame = new Rectangle(0, 0, 200, 7);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        AssertStep(scene);
        Assert.StartsWith("ACTIVITY · inspecting · ", scene.View.PaneTitle(OwnerConsolePane.Activity));
    }

    [Fact]
    public async Task OtherPaneNavigationAndRefresh_LeavesActivityFollowingAtNewestRow()
    {
        using var scene = new Scene();
        Populate(scene, 20);
        await scene.Render(Events(scene, 30));
        SetFrames(scene, 5);
        AssertControl(scene);
        await Step(scene, Key.Tab);
        AssertControl(scene);
        await Step(scene, Key.Tab.WithShift);
        AssertControl(scene);
        await Step(scene, Key.Tab);
        AssertControl(scene);
        await Step(scene, Key.Tab.WithShift);
        AssertControl(scene);
        await scene.Render(Events(scene, 33));
        AssertControl(scene);
        await Step(scene, Key.Tab);
        AssertControl(scene);
    }

    private static async Task Step(Scene scene, Key key, bool commandOpen = false)
    {
        await scene.View.HandleKeyAsync(key);
        AssertStep(scene, commandOpen);
    }

    private static void AssertStep(Scene scene, bool commandOpen = false)
    {
        Assert.Equal(OwnerConsoleActivityTitle.Title(ActivityOverflow(scene), scene.View.ActivityFollowing, 196),
            scene.View.PaneTitle(OwnerConsolePane.Activity));
        Assert.Equal(OwnerConsoleOverflow.Title("DECISIONS", OwnerConsoleOverflow.Of(
            scene.Controller.Model!.Decisions.Length, scene.View.DecisionsPane.Viewport.Y,
            scene.View.DecisionsPane.Viewport.Height), 196), scene.View.PaneTitle(OwnerConsolePane.Decisions));
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        Assert.Equal(OwnerConsoleOverflow.Title("BOARD", OwnerConsoleOverflow.Of(
            scene.Controller.Model.Board.Length, scene.View.BoardPane.RowOffset,
            scene.View.BoardPane.Viewport.Height - headers), 196), scene.View.PaneTitle(OwnerConsolePane.Board));
        if (!commandOpen)
        {
            var answerable = scene.Controller.SelectedDecision is { } decision &&
                OwnerConsoleScreenController.AnswersInConsole(decision);
            Assert.Equal(OwnerConsoleKeyHints.Hint(scene.View.FocusedPane, answerable, scene.View.ActivityFollowing),
                scene.View.HintText);
        }
    }

    private static void AssertControl(Scene scene)
    {
        AssertStep(scene);
        Assert.NotEqual(OwnerConsolePane.Activity, scene.View.FocusedPane);
        Assert.True(scene.View.ActivityFollowing);
        Assert.Equal(0, scene.View.ActivityPane.SelectedItem);
        Assert.Equal(0, scene.View.ActivityPane.Viewport.Y);
        Assert.StartsWith("ACTIVITY · following · ", scene.View.PaneTitle(OwnerConsolePane.Activity));
        Assert.EndsWith(" below", scene.View.PaneTitle(OwnerConsolePane.Activity));
    }

    private static OwnerConsoleOverflow ActivityOverflow(Scene scene) => OwnerConsoleOverflow.Of(
        scene.View.ActivityLines.Count, scene.View.ActivityPane.Viewport.Y, scene.View.ActivityPane.Viewport.Height);

    private static OwnerConductEvent[] Events(Scene scene, int count)
    {
        var time = scene.Harness.Clock.GetUtcNow();
        return Enumerable.Range(0, count).Select(index =>
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
