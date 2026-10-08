using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each test owns its kernel, clock, view and dialog capture.
public sealed class OwnerConsoleActivityOutcomeTests
{
    [Fact]
    public async Task ThreeLandingsAreOneOutcomeAndSuccessfulRestartIsSilent()
    {
        using var scene = new Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        OwnerConductEvent[] events =
        [
            new(time, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED"),
            new(time, "loop-relaunch", "22222222", "LOOP_RELAUNCH_SCHEDULED"),
            new(time, "loop-relaunch", "33333333", "LOOP_RELAUNCH_SCHEDULED"),
            new(time.AddSeconds(1), "loop-handoff", null, "ACTIVATION_ADOPTED reason=none")
        ];
        await scene.Render(events);
        Assert.Equal($"{time.ToLocalTime():HH:mm:ss} Landed together: Search (11111111), Export (22222222), Help (33333333)",
            Assert.Single(scene.View.ActivityLines));
    }

    [Fact]
    public async Task JointFailureAndRetryAreOneOutcomeNamingBothGoals()
    {
        using var scene = new Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render([
            new(time, "acceptance-cohort", null, "ACCEPTANCE_COHORT_CHILD_COMPLETED members=11111111-full-goal+22222222-full-goal reason=child-result-published verdict=failed"),
            new(time.AddSeconds(1), "acceptance-cohort", null, "ACCEPTANCE_COHORT tick=4 members=11111111,22222222 outcome=failed attribution=Indeterminate partitions=11111111:Passed,22222222:Passed"),
            new(time.AddSeconds(2), "acceptance-cohort", "11111111", "ACCEPTANCE_COHORT tick=4 result=held"),
            new(time.AddSeconds(2), "acceptance-cohort", "22222222", "ACCEPTANCE_COHORT tick=4 result=held")]);
        Assert.Equal($"{time.AddSeconds(1).ToLocalTime():HH:mm:ss} Joint test run for Search, Export: failed (an unrelated flaky test failed); retrying automatically",
            Assert.Single(scene.View.ActivityLines));
    }

    [Fact]
    public async Task QuestionsAndHoldsShowTheirResolutionInChronologicalOrder()
    {
        using var scene = new Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        scene.Harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Keep this direction?"));
        await scene.Render([]);
        scene.Harness.Questions.Items.Clear();
        await scene.Render([
            new(time, "goal-escalation", "11111111", "steward-owner-question question=Keep this direction?"),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "HumanInputReceived"),
            new(time.AddSeconds(2), "goal-stalled", "11111111", "GOAL_STALLED owner=none repeatedForSeconds=300 blocker=waiting_for_approval"),
            new(time.AddSeconds(2), "goal-escalation", "11111111", "ownerless-hold-stalled heldForSeconds=300 blocker=waiting_for_approval"),
            new(time.AddSeconds(3), "train-receipt-released", "11111111", "result=released")]);
        Assert.Equal(new[] { "Needs you: 11111111 Keep this direction?", "Resolved: 11111111 Keep this direction?",
            "Waiting: Search has been held 5 min: waiting for your approval", "Moving again: Search" },
            scene.Controller.Model!.Activity.Reverse().Select(item => item.Phrase));
    }

    [Fact]
    public async Task RealSingleGoalFailureShowsTheCheckAndDeveloperReturn()
    {
        using var scene = new Scene();
        scene.AddGoals();
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render([
            new(time, "acceptance", "11111111", "ACCEPTANCE goal=11111111 result=failed checks=Build_main"),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "TaskDispatched role=Developer")]);
        Assert.Contains($"{time.ToLocalTime():HH:mm:ss} 11111111 Search: failed its tests (the check Build main failed); sent back to the Developer",
            scene.View.ActivityLines);
        Assert.Contains($"{time.AddSeconds(1).ToLocalTime():HH:mm:ss} 11111111 Developer started on Search", scene.View.ActivityLines);
    }

    [Theory]
    [InlineData("acceptance", "ACCEPTANCE result=passed", "passed its tests, landing next")]
    [InlineData("goal-lifecycle", "TaskFailed role=Reviewer outcome=finding", "a problem needs correction")]
    [InlineData("goal-stalled", "GOAL_STALLED repeatedForSeconds=300 blocker=waiting_for_approval", "waiting for your approval")]
    [InlineData("acceptance-cohort", "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed attribution=FirstMemberFailed", ": failed (its own checks failed)")]
    public async Task LongTitlesKeepTheOutcomeVisibleInTheActivityPane(string kind, string detail, string outcome)
    {
        using var scene = new Scene();
        scene.Harness.AddGoal("11111111", "Search " + new string('x', 150), AgentRole.Developer);
        scene.Harness.AddGoal("22222222", "Export " + new string('y', 150), AgentRole.Developer);
        await scene.Render([new(scene.Harness.Clock.GetUtcNow(), kind, "11111111", detail)]);
        scene.View.Fit(118, 40);
        var line = Assert.Single(scene.View.ActivityLines);
        Assert.Contains(outcome, line[..Math.Min(118, line.Length)]);
        Assert.Contains("…", line);
    }

    [Fact]
    public async Task EnterExplainsTheSelectedPaneLineWithFourParts()
    {
        using var scene = new Scene();
        scene.AddGoals();
        await scene.Render([new(scene.Harness.Clock.GetUtcNow(), "acceptance", "11111111", "result=passed")]);
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Enter);
        var dialog = Assert.Single(scene.Dialogs.Messages);
        Assert.Equal("What this means", dialog.Title);
        var parts = dialog.Text.Split('\n');
        Assert.Equal(4, parts.Length);
        Assert.Equal("What happened: " + Assert.Single(scene.View.ActivityLines), parts[0]);
        Assert.StartsWith("Why: ", parts[1]);
        Assert.StartsWith("What happens next: ", parts[2]);
        Assert.StartsWith("Do you need to act: ", parts[3]);
        Assert.Contains("No.", parts[3]);
    }

    [Fact]
    public async Task EnterOnNoticeOrUnavailableRowDoesNotExplainADifferentEvent()
    {
        using var scene = new Scene();
        await scene.Render([new(scene.Harness.Clock.GetUtcNow(), "loop-start", null, "LOOP_START")]);
        scene.View.ShowRefreshFailure("questions unavailable");
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Tab);
        scene.View.ActivityPane.SelectedItem = 0;
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Empty(scene.Dialogs.Messages);
        scene.View.Render(scene.Controller.Model! with { ActivityState = new(Error: "log unavailable") });
        await scene.View.HandleKeyAsync(Key.CursorDown);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Empty(scene.Dialogs.Messages);
    }

    [Fact]
    public async Task DisappearingQuestionAndClearedHoldAreObservedWithoutNewConductorVocabulary()
    {
        using var scene = new Scene();
        scene.AddGoals();
        var question = new OwnerQuestion("q1", "11111111", OwnerQuestionKind.HumanInput, "Continue?");
        scene.Harness.Questions.Items.Add(question);
        var snapshot = scene.Harness.Kernel.ExportSnapshot();
        scene.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
            { CurrentHold = new GoalHoldSnapshot("h1", "owner-review-hold", "waiting_for_approval", scene.Harness.Clock.GetUtcNow()) }).ToArray() });
        OwnerConductEvent[] events = [
            new(scene.Harness.Clock.GetUtcNow().AddMinutes(-5), "goal-escalation", "11111111", "steward-owner-question question=Continue?"),
            new(scene.Harness.Clock.GetUtcNow().AddMinutes(-4), "goal-stalled", "11111111", "GOAL_STALLED repeatedForSeconds=60 blocker=waiting_for_approval")];
        await scene.Render(events);
        scene.Harness.Questions.Items.Clear();
        scene.Harness.Kernel.ReplaceWithSnapshot(snapshot);
        await scene.Render(events);
        Assert.Contains(scene.Controller.Model!.Activity, item => item.Phrase == "Resolved: 11111111 Continue?");
        Assert.Contains(scene.Controller.Model.Activity, item => item.Phrase == "Moving again: Search");
    }

    internal sealed class Scene : IDisposable
    {
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly DialogCapture Dialogs = new();
        internal readonly IApplication App = Terminal.Gui.App.Application.Create();
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;
        internal Scene(IGoalEventTail? tail = null, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null,
            Func<OwnerConsoleHarness, IOwnerQuestionSource>? questions = null)
        {
            var source = questions?.Invoke(Harness) ?? Harness.Questions;
            Builder = new(Harness.State, source, Harness.Liveness, new Epics(), Harness.Clock, evidence);
            Controller = new(source, Harness.Answers, Dialogs, Harness.State, tail ?? Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock);
            View = new(App, Controller, () => Task.CompletedTask);
        }
        internal void AddGoals()
        {
            Harness.AddGoal("11111111", "Search", AgentRole.Developer);
            Harness.AddGoal("22222222", "Export", AgentRole.Developer);
            Harness.AddGoal("33333333", "Help", AgentRole.Developer);
        }
        internal async Task Render(IReadOnlyList<OwnerConductEvent> events) =>
            View.Render(await Builder.BuildAsync(new(Harness.Clock.GetUtcNow(), null, events, 0)));
        public void Dispose() { View.Dispose(); App.Dispose(); }
    }
    internal sealed class DialogCapture : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Messages = [];
        public Task ShowTextAsync(string title, string text) { Messages.Add((title, text)); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException();
    }
    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
