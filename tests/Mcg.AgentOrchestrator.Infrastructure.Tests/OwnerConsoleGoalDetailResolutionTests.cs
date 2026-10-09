using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: unique stores and local snapshot state, with no initialized terminal.
public sealed class OwnerConsoleGoalDetailResolutionTests
{
    [Fact(DisplayName = "Goal detail lists resolutions newest first and Enter opens the selected answer")]
    public async Task GoalDetailListsQuestionsAndEnterOpensResolution()
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        var old = await scene.ResolveSpec("Earlier question?\nOlder context", "First answer", OwnerQuestionResolutionDialogTests.Scene.At);
        var newerTime = OwnerQuestionResolutionDialogTests.Scene.At.AddMinutes(1);
        var newest = await scene.ResolveSpec("Newest question?\nNew context", "Second answer", newerTime);
        scene.Dialogs.OnPage = (text, choiceLines) =>
        {
            Assert.Equal(2, choiceLines.Count);
            var rows = text.Replace("\r", "").Split('\n');
            var first = rows[choiceLines[0]];
            var second = rows[choiceLines[1]];
            Assert.Contains("spec clarification before Planner dispatch: Newest question?", first);
            Assert.Contains("the Author, " + OwnerQuestionResolutionDialogTests.Scene.Local(newerTime), first);
            Assert.Contains("spec clarification before Planner dispatch: Earlier question?", second);
            Assert.Contains("the Author, " + OwnerQuestionResolutionDialogTests.Scene.Local(OwnerQuestionResolutionDialogTests.Scene.At), second);
            using var dialog = new OwnerConsoleTextDialog("Goal", text, choiceLines);
            dialog.Format(60, 10);
            var firstRow = dialog.Page.SourceLines.ToList().IndexOf(choiceLines[0]);
            for (var row = 0; row < firstRow; row++) Assert.True(dialog.HandleKey(Key.CursorDown));
            int? selected = null;
            dialog.ChoiceAccepted += index => selected = index;
            Assert.True(dialog.HandleKey(Key.Enter));
            return selected;
        };
        await scene.Controller.ShowGoalDetailAsync(scene.Goal.Id.Value);
        Assert.Equal(2, scene.Dialogs.Texts.Count);
        Assert.Equal("Question resolution", scene.Dialogs.Texts[1].Title);
        var expected = (await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, CancellationToken.None)).First();
        Assert.Equal(newest, expected.Id);
        Assert.NotEqual(old, expected.Id);
        Assert.Equal(OwnerQuestionResolutionText.Build(expected, scene.Harness.Clock.LocalTimeZone), scene.Dialogs.Texts[1].Text);
        Assert.Contains("Second answer", scene.Dialogs.Texts[1].Text);
    }

    [Theory(DisplayName = "Goal detail states the first recorded send-back finding and collapses equal status and stage")]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public async Task SendBackShowsFindingAndOmitsDuplicateState(AgentRole role)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("22222222-detail", "Finding detail", role);
        var time = OwnerQuestionResolutionDialogTests.Scene.At;
        var finding = new ReviewFinding("finding1", ReviewFindingState.Open,
            new("src/example.cs", "Example"), "The answer loses its evidence.\nMore detail.");
        var verification = new TaskVerificationSnapshot("review", ".", 1, "", "", time,
            MergedReviewFindings: [finding]);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            Status = GoalStatus.Verified,
            Tasks = item.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed,
                LastVerification = verification, VerificationHistory = [verification] }).ToArray()
        }).ToArray() });
        var tail = new OwnerQuestionResolutionDialogTests.TailSource();
        tail.Lines.Add(OwnerQuestionResolutionDialogTests.Scene.Lifecycle(time, "TaskFailed", goal.Tasks[0].Id.Value));
        var dialogs = new OwnerQuestionResolutionDialogTests.CapturingDialogs();
        using var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
            harness.State, tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        await controller.ShowGoalDetailAsync(goal.Id.Value);
        var text = Assert.Single(dialogs.Texts).Text;
        Assert.Contains(role + " sent back: The answer loses its evidence.", text);
        Assert.DoesNotContain("a problem needs correction", text);
        Assert.Contains("Status: Verified", text);
        Assert.DoesNotContain("Stage: Verified", text);
        Assert.DoesNotContain("Role: -", text);
    }
}
