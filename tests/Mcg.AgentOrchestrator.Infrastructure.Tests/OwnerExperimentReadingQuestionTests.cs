using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its database directory, state, dialogs and controller.
public sealed class OwnerExperimentReadingQuestionTests
{
    private const string StopId = "1a2b3c4d000000000000000000000000";
    private const string GuardId = "5e6f7a8b000000000000000000000000";

    [Fact]
    public async Task ReadAsync_StopRuleItem_SurfacesChoicesWithoutGoalOrInternalTerms()
    {
        using var scene = new Scene();
        var item = await scene.RaiseAsync(StopId, "stop-rule");

        var snapshot = await scene.Source.ReadAsync(CancellationToken.None);

        var question = Assert.Single(snapshot.Live);
        Assert.Empty(snapshot.Hidden);
        Assert.Equal(item.Id, question.ItemId);
        Assert.Equal(OwnerQuestionKind.ExperimentReading, question.Kind);
        Assert.Equal("", question.GoalId);
        Assert.StartsWith("Experiment 1a2b3c4d reading due: stop rule reached", question.Text);
        Assert.Equal(new[]
        {
            "Experiment 1a2b3c4d reading due: stop rule reached",
            "keep: confirm the result with experiment-decide 1a2b3c4d --outcome confirmed --evidence <reference> --action <text>",
            "revert: refute it with experiment-decide 1a2b3c4d --outcome refuted --evidence <reference> --action <text>",
            "extend: do nothing now; the question stays open until you decide",
            "show: run experiment-show 1a2b3c4d to print the reading"
        }, question.Text.Split('\n'));
        Assert.DoesNotContain(StopId, question.Text);
        foreach (var forbidden in new[] { "none", "child result", "handoff", "cohort", "receipt", "canary", "tick" })
            Assert.DoesNotContain(forbidden, question.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ResolvedStopRule_DropsItAndRetainsGuardrailQuestion()
    {
        using var scene = new Scene();
        var stop = await scene.RaiseAsync(StopId, "stop-rule");
        var guard = await scene.RaiseAsync(GuardId, "guardrail");
        var before = await scene.Source.ReadAsync(CancellationToken.None);
        Assert.Equal(2, before.Live.Count);
        var stopQuestion = Assert.Single(before.Live, item => item.ItemId == stop.Id);
        var guardQuestion = Assert.Single(before.Live, item => item.ItemId == guard.Id);
        Assert.StartsWith("Experiment 5e6f7a8b reading due: guardrail breached", guardQuestion.Text);
        Assert.NotEqual(stopQuestion.Text, guardQuestion.Text);

        Assert.True(await scene.Store.TryResolveAsync(stop.CorrelationKey!, "confirmed"));
        var after = await scene.Source.ReadAsync(CancellationToken.None);

        Assert.Equal(guardQuestion, Assert.Single(after.Live));
        Assert.Empty(after.Hidden);
    }

    [Fact]
    public async Task BuildAsync_GoalLessReading_PreservesClarificationAndCountsBoth()
    {
        using var scene = new Scene();
        var clarification = new OwnerQuestion("clarification", scene.Goal.Id.Value,
            OwnerQuestionKind.Clarification, "Which scope?", "repository", "high", "keep scope");
        scene.Harness.Questions.Items.Add(clarification);
        var builder = scene.Builder();
        var inputs = new OwnerConsoleViewInputs(scene.Harness.Clock.GetUtcNow(), null, [], 0);
        var baseline = await builder.BuildAsync(inputs);
        var baselineDecision = Assert.Single(baseline.Decisions);
        var baselineBoard = Assert.Single(baseline.Board);

        // Hydrate the reading first so restoring the read-model filter breaks this integration path.
        await scene.RaiseAsync(StopId, "stop-rule");
        var reading = Assert.Single((await scene.Source.ReadAsync(CancellationToken.None)).Live);
        scene.Harness.Questions.Items.Add(reading);
        var model = await builder.BuildAsync(inputs);

        Assert.Equal(2, model.Decisions.Length);
        Assert.Equal("", Assert.Single(model.Decisions, row => row.Id == reading.ItemId).GoalPrefix);
        Assert.Equal(baselineDecision, Assert.Single(model.Decisions, row => row.Id == clarification.ItemId));
        Assert.Equal(baselineBoard, Assert.Single(model.Board));
        Assert.Equal(OwnerConsoleStageDescriber.OwnerQuestion, Assert.Single(model.Board).Stage);
        Assert.Equal(2, model.Status.LiveDecisions);
        Assert.Equal(0, model.Status.HiddenQuestions);
        Assert.Contains(model.Activity, row => row.OwnerQuestionId == reading.ItemId);
    }

    [Fact]
    public async Task HandleKeyAsync_Reading_RefusesAnswersAndSkipsGoalResolution()
    {
        using var scene = new Scene();
        await scene.RaiseAsync(StopId, "stop-rule");
        var reading = Assert.Single((await scene.Source.ReadAsync(CancellationToken.None)).Live);
        // A default ensures an unguarded accept path would submit, rather than stop at no-default.
        scene.Harness.Questions.Items.Add(reading with { ProposedDefault = "keep" });
        var dialogs = new Dialogs();
        var probe = new OwnerConsoleHarness();
        var reader = new OwnerQuestionResolutionReader(probe.State, probe.Tail,
            scene.Root, scene.Root, probe.Clock);
        using var controller = new OwnerConsoleScreenController(scene.Harness.Questions,
            scene.Harness.Answers, dialogs, scene.Harness.State, scene.Harness.Tail,
            scene.Harness.Conductor, scene.Harness.DigestReport, scene.Harness.Digest,
            scene.Harness.Clock, resolutions: reader);
        var builder = scene.Builder();
        var inputs = new OwnerConsoleViewInputs(scene.Harness.Clock.GetUtcNow(), null, [], 0);
        controller.Apply(await builder.BuildAsync(inputs));
        Assert.False(OwnerConsoleScreenController.AnswersInConsole(controller.SelectedDecision!));

        await controller.HandleKeyAsync(ConsoleKey.R, 'r');
        await controller.HandleKeyAsync(ConsoleKey.A, 'a');

        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Equal(0, dialogs.PromptCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(2, dialogs.Texts.Count);
        Assert.All(dialogs.Texts, notice =>
        {
            Assert.Equal("View only", notice.Title);
            Assert.Contains("experiment-show", notice.Text);
            Assert.Contains("experiment-decide", notice.Text);
            Assert.DoesNotContain("goal", notice.Text, StringComparison.OrdinalIgnoreCase);
        });

        await controller.HandleKeyAsync(ConsoleKey.Enter);
        Assert.Equal(1, dialogs.DecisionCalls);
        Assert.Contains(reading.Text, dialogs.DetailText);
        Assert.Equal("No longer open", dialogs.Banner);
        Assert.DoesNotContain("goals", probe.State.Calls);

        scene.Harness.Questions.Items.Clear();
        scene.Harness.Questions.Items.Add(new("steward", scene.Goal.Id.Value,
            OwnerQuestionKind.StewardHold, "Review the hold"));
        controller.Apply(await builder.BuildAsync(inputs));
        await controller.HandleKeyAsync(ConsoleKey.R, 'r');
        Assert.Equal("Steward questions are answered through goal verbs for now; use the CLI retry/adjudicate commands for goal 11111111-first.",
            dialogs.Texts.Last().Text);
        Assert.Empty(scene.Harness.Answers.Calls);
        // Positive control: a goal-backed dialog uses the same reader probe.
        await controller.HandleKeyAsync(ConsoleKey.Enter);
        Assert.Contains("goals", probe.State.Calls);
    }

    [Theory]
    [InlineData("experiment-reading-due:short:stop-rule")]
    [InlineData("experiment-reading-due:1a2b3c4d000000000000000000000000:unknown")]
    [InlineData("experiment-reading-due:1a2b3c4d000000000000000000000000:stop-rule:extra")]
    [InlineData("unrelated:1a2b3c4d000000000000000000000000:stop-rule")]
    [InlineData(null)]
    public async Task ReadAsync_UnrecognisedKey_SkipsItemWithoutHiddenQuestion(string? key)
    {
        using var scene = new Scene();
        var item = await scene.Store.RaiseAsync(CollaborationItemType.Decision, null,
            "Experiment 1a2b3c4d reading due (stop-rule)", "Question: Do not parse the body", key);
        Assert.Null(OwnerExperimentReadingQuestion.From(item));

        var snapshot = await scene.Source.ReadAsync(CancellationToken.None);

        Assert.Empty(snapshot.Live);
        Assert.Empty(snapshot.Hidden);
    }

    [Fact]
    public async Task From_ProducerFields_UsesKeyAndRequiresDecisionWithoutGoal()
    {
        using var scene = new Scene();
        var item = await scene.RaiseAsync(StopId, "stop-rule");
        Assert.Equal(OwnerExperimentReadingQuestion.From(item),
            OwnerExperimentReadingQuestion.From(item with { Subject = "unrelated", Body = "unrelated" }));
        Assert.Null(OwnerExperimentReadingQuestion.From(item with { Type = CollaborationItemType.Clarification }));
        Assert.Null(OwnerExperimentReadingQuestion.From(item with { GoalId = scene.Goal.Id.Value }));
        Assert.NotNull(OwnerExperimentReadingQuestion.From(item with { GoalId = " " }));
    }

    private sealed class Scene : IDisposable
    {
        internal readonly string Root = SharedTestSupport.CreateTempDirectory();
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly CollaborationItemStore Store;
        internal readonly OwnerQuestionReadModel Source;
        internal readonly Goal Goal;

        internal Scene()
        {
            Goal = Harness.AddGoal("11111111-first", "Unrelated active work", AgentRole.Developer);
            Store = new CollaborationItemStore(Path.Combine(Root, "collaboration-items.db"));
            Source = new OwnerQuestionReadModel(Harness.State, Root);
        }

        internal Task<CollaborationItem> RaiseAsync(string id, string trigger) =>
            Store.RaiseAsync(CollaborationItemType.Decision, null,
                $"Experiment {id[..8]} reading due ({trigger})", $"Experiment {id}",
                $"experiment-reading-due:{id}:{trigger}");

        internal OwnerConsoleViewModelBuilder Builder() =>
            new(Harness.State, Harness.Questions, Harness.Liveness, new Epics(), Harness.Clock);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        internal int PromptCalls;
        internal int ConfirmCalls;
        internal int DecisionCalls;
        internal string DetailText = "";
        internal string Banner = "";

        public Task<string?> PromptTextAsync(string title, string text)
        { PromptCalls++; return Task.FromResult<string?>("keep"); }
        public Task<bool> ConfirmAsync(string title, string text)
        { ConfirmCalls++; return Task.FromResult(true); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
        public async Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail)
        {
            DecisionCalls++;
            DetailText = detail.State.Text;
            // Observe disappearance and await its refresh to exercise the resolution delegate.
            detail.Observe(null);
            await detail.LastRefresh.WaitAsync(TestContext.Current.CancellationToken);
            Banner = detail.State.Banner;
        }
    }
}
