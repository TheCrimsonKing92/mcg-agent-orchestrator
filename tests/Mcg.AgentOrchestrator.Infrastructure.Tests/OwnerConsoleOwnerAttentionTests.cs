using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Input;

// Parallel-safe: all observations, dialogs and clocks belong to the scene.
public sealed class OwnerConsoleOwnerAttentionTests
{
    [Fact]
    public async Task OnlyTheLiveOwnerRoutedItemRaisesAttentionAndRemovalResolvesIt()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene(questions: harness =>
                new OwnerQuestionReadModel(harness.State, root));
            scene.AddGoals();
            var time = scene.Harness.Clock.GetUtcNow();
            var kernel = scene.Harness.Kernel;
            var clarification = kernel.RequestHumanInput(new("11111111"), null, "Which implementation?");
            kernel.SubmitHumanInput(clarification.Id, "Use the existing seam.");
            kernel.ObserveGoalHold(new("22222222"), "author-owner-question", "question=Pick a lane?",
                time, TimeSpan.MaxValue, "q2");
            kernel.ObserveGoalHold(new("33333333"), "steward-owner-question",
                "question=Ship now?\nThe risks are recorded. evidence=[proof]", time, TimeSpan.MaxValue, "q3");
            var snapshot = await new OwnerQuestionReadModel(scene.Harness.State, root).ReadAsync(CancellationToken.None);
            Assert.True(clarification.IsCompleted);
            Assert.Equal("author-owner-question", kernel.GetGoal(new("22222222")).CurrentHold!.State);
            Assert.Equal(kernel.GetGoal(new("33333333")).CurrentHold!.Identity, Assert.Single(snapshot.Live).ItemId);
            OwnerConductEvent[] events = [
                new(time.AddSeconds(-3), "author", "11111111", "kind=answer question=Which implementation?"),
                new(time.AddSeconds(-2), "goal-lifecycle", "11111111", "HumanInputReceived"),
                new(time.AddSeconds(-1), "goal-escalation", "22222222", "author-owner-question question=Pick a lane?"),
                new(time, "goal-escalation", "33333333", "steward-owner-question question=Ship now? evidence=proof")];
            await scene.Render(events);
            var needs = Assert.Single(scene.View.ActivityLines, line => line.Contains("Needs you:"));
            Assert.Contains("Needs you: 33333333 Ship now?", needs);
            Assert.DoesNotContain("The risks", needs);
            Assert.DoesNotContain(scene.View.ActivityLines, line => line.Contains("Resolved:"));
            Assert.Single(scene.Controller.Model!.Decisions);
            var escalation = Assert.Single(scene.Controller.Model.Activity, item => item.Kind == "goal-escalation");
            Assert.Contains("22222222 question sent to the operator: Pick a lane?", escalation.Phrase);
            await scene.Controller.ShowActivityMeaningAsync(escalation);
            Assert.Contains("Do you need to act: No.", scene.Dialogs.Messages[^1].Text);

            kernel.ClearGoalHold(new("33333333"));
            await scene.Render(events.Append(new(time.AddSeconds(1), "goal-lifecycle", "33333333", "HumanInputReceived")).ToArray());
            Assert.Contains("Resolved: 33333333 Help: Ship now?", Assert.Single(scene.View.ActivityLines, line => line.Contains("Resolved:")));
            await scene.Render(events);
            Assert.Single(scene.View.ActivityLines, line => line.Contains("Resolved:"));
            Assert.Empty(scene.Controller.Model!.Decisions);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task LiveClarificationsUseDecisionsMembershipWithoutAnOperatorCompletionMarker()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene(questions: harness =>
                new OwnerQuestionReadModel(harness.State, root));
            scene.AddGoals();
            var request = scene.Harness.Kernel.RequestHumanInput(new("11111111"), null, "Which implementation?");
            var clarification = await CollaborationItemStore.ForDirectory(root).RaiseAsync(
                CollaborationItemType.Clarification, "22222222", "Choose scope", "Question: Which scope?",
                "spec-clarification:owner-attention");

            // Live membership is the operator's routing fact, including both clarification sources.
            // No Author event, operator-turn marker or elapsed-time condition is necessary.
            await scene.Render([]);
            var model = scene.Controller.Model!;
            Assert.Equal(2, model.Decisions.Length);
            Assert.Equal(new[] { request.Id.Value, clarification.Id }.Order(), model.Decisions.Select(item => item.Id).Order());
            Assert.Equal(2, scene.View.ActivityLines.Count(line => line.Contains("Needs you:")));
            Assert.Contains(scene.View.ActivityLines, line => line.Contains("Needs you: 11111111 Which implementation?"));
            Assert.Contains(scene.View.ActivityLines, line => line.Contains("Needs you: 22222222 Which scope?"));
            foreach (var item in model.Activity)
            {
                var decision = Assert.Single(model.Decisions, row => row.Id == item.OwnerQuestionId);
                await scene.Controller.ShowActivityMeaningAsync(item);
                var text = scene.Dialogs.Messages[^1].Text;
                Assert.Contains("still waiting on you", text);
                Assert.Contains("Do you need to act: Yes.", text);
                Assert.Contains($"DECISIONS row [{decision.Number}] {decision.GoalPrefix} {decision.Kind}", text);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
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
