using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

// Parallel-safe: all observations, dialogs and clocks belong to the scene.
public sealed class OwnerConsoleOwnerAttentionTests
{
    [Fact]
    public async Task OnlyTheLiveOwnerRoutedItemRaisesAttentionAndRemovalResolvesIt()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        OwnerConductEvent[] events = [
            new(time.AddSeconds(-3), "author", "11111111", "kind=answer question=Which implementation?"),
            new(time.AddSeconds(-2), "goal-lifecycle", "11111111", "HumanInputReceived"),
            new(time.AddSeconds(-1), "goal-escalation", "22222222", "author-owner-question question=Pick a lane?"),
            new(time, "goal-escalation", "33333333", "steward-owner-question question=Ship now? evidence=proof")];
        scene.Harness.Questions.Items.Add(new("q3", "33333333", OwnerQuestionKind.StewardHold, "Ship now?\nThe risks are recorded."));
        await scene.Render(events);
        var needs = Assert.Single(scene.View.ActivityLines, line => line.Contains("Needs you:"));
        Assert.Contains("Needs you: 33333333 Ship now?", needs);
        Assert.DoesNotContain("The risks", needs);
        Assert.DoesNotContain(scene.View.ActivityLines, line => line.Contains("Resolved:"));
        Assert.Single(scene.Controller.Model!.Decisions);

        scene.Harness.Questions.Items.Clear();
        await scene.Render(events.Append(new(time.AddSeconds(1), "goal-lifecycle", "33333333", "HumanInputReceived")).ToArray());
        Assert.Contains("Resolved: 33333333 Ship now?", Assert.Single(scene.View.ActivityLines, line => line.Contains("Resolved:")));
        await scene.Render(events);
        Assert.Single(scene.View.ActivityLines, line => line.Contains("Resolved:"));
    }

    [Fact]
    public async Task MeaningNamesTheSubjectAndChecksTheSameLiveDecisionsItem()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        const string reason = "plan rejected: cited a file that does not exist: .config/dotnet-tools.json";
        scene.Harness.Questions.Items.Add(new("q2", "22222222", OwnerQuestionKind.HumanInput, "Ship the export?"));
        await scene.Render([
            new(time.AddSeconds(-3), "goal-escalation", "11111111", "GOAL result=escalated reason=" + reason),
            new(time.AddSeconds(-2), "goal-lifecycle", "11111111", "TaskDispatched role=Planner"),
            new(time, "goal-escalation", "22222222", "steward-owner-question question=Ship the export?")]);
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        var items = scene.Controller.Model!.Activity;
        scene.View.ActivityPane.SelectedItem = Array.FindIndex(items.ToArray(), item => item.Kind == "goal-escalation");
        await scene.View.HandleKeyAsync(Key.Enter);
        var retried = scene.Dialogs.Messages[^1].Text;
        Assert.Contains("Search", retried);
        Assert.Contains(reason, retried);
        Assert.Contains($"retried automatically at {time.AddSeconds(-2).ToLocalTime():HH:mm:ss}", retried);
        Assert.Contains("Do you need to act: No.", retried);
        Assert.All(retried.Split('\n'), line => Assert.Contains("11111111", line));

        scene.View.ActivityPane.SelectedItem = Array.FindIndex(items.ToArray(), item => item.OwnerQuestionId == "q2");
        await scene.View.HandleKeyAsync(Key.Enter);
        var open = scene.Dialogs.Messages[^1].Text;
        Assert.Contains("Export", open);
        Assert.Contains("Ship the export?", open);
        Assert.Contains("still waiting on you", open);
        Assert.Contains("Do you need to act: Yes.", open);
        Assert.Contains("DECISIONS row [1] 22222222 HumanInput", open);
        Assert.All(open.Split('\n'), line => Assert.Contains("22222222", line));

        // A stale screen cannot turn a closed item into a live decision at Enter time.
        scene.Harness.Questions.Items.Clear();
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Contains("Do you need to act: No.", scene.Dialogs.Messages[^1].Text);
        Assert.DoesNotContain("still waiting", scene.Dialogs.Messages[^1].Text);
        await scene.Render([]);
        scene.View.ActivityPane.SelectedItem = Array.FindIndex(scene.Controller.Model!.Activity.ToArray(), item => item.Kind == "owner-question");
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Contains("resolved at", scene.Dialogs.Messages[^1].Text);
    }

    [Fact]
    public async Task RecordedAnswerActorAndTimeAppearInTheResolvedQuestionMeaning()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        scene.Harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Continue?"));
        await scene.Render([]);
        scene.Harness.Questions.Items.Clear();
        var time = scene.Harness.Clock.GetUtcNow();
        var answer = System.Text.Json.JsonSerializer.Serialize(new { timestamp = time, eventType = "GoalPolicyDecision",
            message = "applied verb=answer target=HumanInput:q1", operatorIntentApplied = new { verb = "answer", actor = "author" } });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(answer, "11111111", out var parsed));
        await scene.Render([parsed!]);
        var question = Assert.Single(scene.Controller.Model!.Activity, item => item.Kind == "owner-question");
        await scene.Controller.ShowActivityMeaningAsync(question);
        var text = Assert.Single(scene.Dialogs.Messages).Text;
        Assert.Contains($"resolved at {time.ToLocalTime():HH:mm:ss} by the Author", text);
        Assert.Contains("Do you need to act: No.", text);
        Assert.DoesNotContain("Work waits for your answer", text);
    }

    [Fact]
    public async Task AnotherQuestionOnTheSameGoalNeverMakesAnOldEventActionable()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        scene.AddGoals();
        var question = new OwnerQuestion("old", "11111111", OwnerQuestionKind.HumanInput, "Continue?");
        scene.Harness.Questions.Items.Add(question);
        await scene.Render([]);
        var old = Assert.Single(scene.Controller.Model!.Activity);
        scene.Harness.Questions.Items.Clear();
        scene.Harness.Questions.Items.Add(question with { ItemId = "new" });
        await scene.Render([]);
        await scene.Controller.ShowActivityMeaningAsync(old);
        Assert.Contains("Do you need to act: No.", scene.Dialogs.Messages[^1].Text);
        Assert.DoesNotContain("still waiting", scene.Dialogs.Messages[^1].Text);
        Assert.Equal(2, scene.Controller.Model!.Activity.Count(item => item.Kind == "owner-question"));
        Assert.Single(scene.Controller.Model.Activity, item => item.Kind == "owner-question-resolved");
    }
}
