using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

public sealed class OwnerConsolePaneJumpTests
{
    [Fact]
    public async Task EveryPaneMovesSelectionByVisiblePageAndEnterUsesThatRow()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        var time = scene.Harness.Clock.GetUtcNow();
        for (var index = 0; index < 12; index++)
        {
            var id = index.ToString("D8");
            scene.Harness.AddGoal(id, "Goal " + index, AgentRole.Developer);
            scene.Harness.Questions.Items.Add(new("q" + index, id, OwnerQuestionKind.HumanInput, "Question " + index));
        }
        await scene.Render(Enumerable.Range(0, 30).Select(index => new OwnerConductEvent(time.AddSeconds(index),
            "acceptance", "00000000", "result=passed")).ToArray());
        scene.View.DecisionsPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 5 + headers);
        for (var pane = 0; pane < 3; pane++)
        {
            Assert.Contains("Home/End", scene.View.HintText);
            Assert.Contains("PgUp/PgDn", scene.View.HintText);
            var count = pane == 0 ? 12 : pane == 1 ? 12 : scene.View.ActivityLines.Count;
            int Selection() => pane == 0 ? scene.Controller.SelectedIndex : pane == 1 ?
                scene.View.BoardPane.Value!.SelectedCell.Y : scene.View.ActivityPane.SelectedItem!.Value;
            await scene.View.HandleKeyAsync(Key.End);
            Assert.Equal(count - 1, Selection());
            await scene.View.HandleKeyAsync(Key.Home);
            Assert.Equal(0, Selection());
            await scene.View.HandleKeyAsync(Key.PageDown);
            Assert.Equal(5, Selection());
            await scene.View.HandleKeyAsync(Key.Enter);
            var text = scene.Dialogs.Messages[^1].Text;
            if (pane == 0) Assert.Contains("Question 5", text);
            else if (pane == 1) Assert.Contains("Title: Goal 5", text);
            else Assert.Contains(OwnerActivityNarrator.Line(scene.Controller.Model!.Activity[5]), text);
            await scene.View.HandleKeyAsync(Key.PageUp);
            Assert.Equal(0, Selection());
            await scene.View.HandleKeyAsync(Key.Tab);
        }
        await scene.View.HandleKeyAsync(new Key('?'));
        var help = scene.Dialogs.Messages[^1];
        Assert.Equal("Help", help.Title);
        Assert.Contains("Home/End", help.Text);
        Assert.Contains("PgUp/PgDn", help.Text);
    }
}
