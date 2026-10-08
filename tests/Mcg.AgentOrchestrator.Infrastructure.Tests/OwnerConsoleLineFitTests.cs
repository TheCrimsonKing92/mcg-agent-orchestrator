using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Text;

public sealed class OwnerConsoleLineFitTests
{
    [Fact]
    public async Task ActivityAndBoardFitWideNarrowAndResizeFromTheFullModel()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        var title = "Make owner console activity understandable " + new string('x', 60);
        scene.Harness.AddGoal("11111111", title, AgentRole.Developer);
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render([
            new(time, "goal-lifecycle", "11111111", "TaskCompleted role=Developer"),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "TaskFailed role=Planner rejection=missing tools.json")]);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 8);
        Assert.All(scene.View.ActivityLines, line => { Assert.Contains(title, line); Assert.DoesNotContain("…", line); });
        Assert.Equal(title, scene.View.BoardTable.Rows[0]["TITLE"]);
        scene.View.ActivityPane.SelectedItem = 1;

        // Frame/viewport changes drive the production resize handlers, without a second model build.
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 80, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 80, 8);
        Assert.All(scene.View.ActivityLines, line =>
        {
            Assert.Equal(80, line.GetColumns());
            Assert.Contains("11111111", line);
            Assert.Contains("…", line);
        });
        Assert.Contains($"{time.ToLocalTime():HH:mm:ss}", scene.View.ActivityLines[1]);
        Assert.Contains("Developer passed", scene.View.ActivityLines[1]);
        Assert.Contains($"{time.AddSeconds(1).ToLocalTime():HH:mm:ss}", scene.View.ActivityLines[0]);
        Assert.Contains("sent", scene.View.ActivityLines[0]);
        Assert.Contains("back: plan rejected: missing tools.json", scene.View.ActivityLines[0]);
        Assert.Equal(1, scene.View.ActivityPane.SelectedItem);
        var boardTitle = Assert.IsType<string>(scene.View.BoardTable.Rows[0]["TITLE"]);
        Assert.Contains("…", boardTitle);
        Assert.Equal(scene.View.BoardPane.Style.GetColumnStyleIfAny(2)!.MaxWidth, boardTitle.GetColumns());
        Assert.Equal(title, scene.Controller.Model!.Board[0].Title);
        Assert.All(scene.Controller.Model.Activity, item => Assert.Contains(title, item.Phrase));

        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 8);
        Assert.All(scene.View.ActivityLines, line => Assert.Contains(title, line));
        Assert.Equal(title, scene.View.BoardTable.Rows[0]["TITLE"]);
    }

    [Fact]
    public void FittingCountsTerminalCellsAndKeepsTheOutcomeAfterAWideUnicodeTitle()
    {
        var title = string.Concat(Enumerable.Repeat("界", 40));
        var line = "12:34:56 11111111 Developer passed " + title;
        var fit = OwnerConsoleLineFitter.Fit(line, [title], 80);
        Assert.Equal(80, fit.GetColumns());
        Assert.StartsWith("12:34:56 11111111 Developer passed ", fit);
        Assert.EndsWith("…", fit);
    }
}
