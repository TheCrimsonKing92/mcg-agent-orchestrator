using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real answer files and registry are fixture-owned; dialogs and intents are local fakes.
public sealed class OnboardingQuestionSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Decisions_RenderProjectAndKey_ConsoleRefusalIsInline_ValidWriteMatchesCliBytes()
    {
        using var fixture = new OnboardingConsoleFixture();
        var console = fixture.Register("conduct-tail");
        var cli = fixture.Register("cli-control");
        var model = OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey);
        OnboardingConsoleFixture.Store(console, model);
        OnboardingConsoleFixture.Store(cli, model);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var refreshes = 0;
        var source = new OnboardingQuestionSource(harness.Questions,
            () => [OnboardingConsoleFixture.Describe(console)], fixture.Clock, () => refreshes++);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, source, harness.Liveness, new Epics(), fixture.Clock);
        var (decisions, hidden) = await builder.ReadDecisionsAsync(Token);
        var decision = Assert.Single(decisions);
        Assert.Equal(OwnerQuestionKind.Onboarding, decision.Kind);
        Assert.Empty(decision.GoalId);
        Assert.Equal("conduct-tail", decision.GoalPrefix);
        Assert.Contains(OnboardingConsoleFixture.TestKey, decision.Summary);
        Assert.Contains("Which command should run?", decision.FullText);
        Assert.Contains("Current value: npm run old-test", decision.FullText);
        Assert.Contains("Suggested value: npm test", decision.FullText);
        var pane = new OwnerConsoleDecisionsPane();
        try
        {
            pane.Render(builder.WithDecisions(await builder.BuildBoardAsync(Token), decisions, hidden), 0, 240);
            var line = Assert.Single(pane.Lines);
            Assert.Contains("conduct-tail Onboarding:", line);
            Assert.Contains(OnboardingConsoleFixture.TestKey, line);
        }
        finally { pane.List.Dispose(); pane.EmptyLabel.Dispose(); }

        var dialogs = new Dialogs { Answer = "npm test | tee" };
        using var tracking = new AnswerIntentStatusTracker(harness.Answers.ReadStatusAsync, fixture.Clock);
        var workflow = new OwnerDecisionWorkflow(source, harness.Answers, dialogs, tracking, null, fixture.Clock);
        var refusal = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(
            OnboardingConsoleFixture.TestKey, dialogs.Answer)).Message;
        var before = OnboardingConsoleFixture.Snapshot(console);
        string? inline = null;
        await workflow.AnswerAsync(decision, false, null, Token, message => inline = message);
        Assert.Equal(refusal + "\nAttempted answer: npm test | tee", inline);
        Assert.Equal(inline, Assert.Single(dialogs.Texts));
        Assert.Equal(before.OrderBy(pair => pair.Key), OnboardingConsoleFixture.Snapshot(console).OrderBy(pair => pair.Key));
        Assert.Equal(decision.Id, Assert.Single((await source.ReadAsync(Token)).Live).ItemId);
        Assert.Equal(0, refreshes);

        dialogs.Answer = "npm   test -- --runInBand";
        await workflow.AnswerAsync(decision, false, null, Token, message => inline = message);
        Assert.Equal("Owner answer recorded", inline);
        using var output = new StringWriter();
        var exit = ProjectAnswerCommand.Execute(["project", "answer", "cli-control",
            OnboardingConsoleFixture.TestKey, dialogs.Answer], fixture.Registry, fixture.Home, null, output,
            fixture.Clock.GetUtcNow);
        Assert.Equal(0, exit);
        var answerPath = Path.Combine(console.OrchestratorDirectory, OwnerAnswerFile.FileName);
        Assert.Equal(File.ReadAllBytes(Path.Combine(cli.OrchestratorDirectory, OwnerAnswerFile.FileName)), File.ReadAllBytes(answerPath));
        var answer = Assert.Single(OwnerAnswerFile.Read(console.OrchestratorDirectory));
        Assert.Equal("npm test -- --runInBand", answer.Value);
        Assert.Equal(0, answer.RecordedUtc.Millisecond);
        Assert.Empty((await source.ReadAsync(Token)).Live);
        Assert.Equal(ProjectOnboardingStep.ManifestInstalled, OnboardingConsoleFixture.Read(console).CurrentStep);
        Assert.Equal(1, refreshes);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task StaleNonLiveAnswer_IsDismissOnly_RemovalPersistsAndProtectsLiveAnswer()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("stale");
        const string oldKey = "commands/removed/test";
        OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey));
        OwnerAnswerFile.Write(project.OrchestratorDirectory,
            [new(OnboardingConsoleFixture.TestKey, "npm test", null, fixture.Clock.GetUtcNow()),
             new(oldKey, "MTP", null, fixture.Clock.GetUtcNow())]);
        OnboardingConsoleFixture.InstallManifest(project);
        fixture.StartConductor(project);
        Assert.False(OnboardingConsoleFixture.Read(project).IsOnboarding);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var source = new OnboardingQuestionSource(harness.Questions,
            () => [OnboardingConsoleFixture.Describe(project)], fixture.Clock);
        var stale = Assert.Single((await source.ReadAsync(Token)).Live);
        Assert.False(stale.CanAnswer);
        Assert.Contains("Stale answer: MTP", stale.Text);
        var before = File.ReadAllBytes(Path.Combine(project.OrchestratorDirectory, OwnerAnswerFile.FileName));
        var refused = await source.AnswerAsync(stale.ItemId, "VSTest", Token);
        Assert.False(refused.Success);
        Assert.Contains(OwnerAnswerApplier.NotLiveReason, refused.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(project.OrchestratorDirectory, OwnerAnswerFile.FileName)));

        var builder = new OwnerConsoleViewModelBuilder(harness.State, source, harness.Liveness, new Epics(), fixture.Clock);
        var decision = Assert.Single((await builder.ReadDecisionsAsync(Token)).Decisions);
        var dialogs = new Dialogs();
        using var tracking = new AnswerIntentStatusTracker(harness.Answers.ReadStatusAsync, fixture.Clock);
        var workflow = new OwnerDecisionWorkflow(source, harness.Answers, dialogs, tracking, null, fixture.Clock);
        await workflow.AnswerAsync(decision, true, null, Token);
        Assert.Equal("Stale answer dismissed", Assert.Single(dialogs.Texts));
        Assert.Equal(OnboardingConsoleFixture.TestKey, Assert.Single(OwnerAnswerFile.Read(project.OrchestratorDirectory)).FactKey);
        Assert.Empty((await source.ReadAsync(Token)).Live);
        // An old stale item must not delete a now-valid answer for its key.
        Assert.False((await source.DismissAsync(stale.ItemId, Token)).Success);
        Assert.False(ProjectOwnerAnswerRecorder.Remove(project.OrchestratorDirectory, OnboardingConsoleFixture.TestKey).Success);
        Assert.Single(OwnerAnswerFile.Read(project.OrchestratorDirectory));
    }

    [Fact]
    public async Task InvalidLiveStaleAnswer_CanBeReanswered_AndCanceledDialogDoesNotWrite()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("replace");
        OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey));
        OwnerAnswerFile.Write(project.OrchestratorDirectory,
            [new(OnboardingConsoleFixture.TestKey, "npm test | tee", null, fixture.Clock.GetUtcNow())]);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var source = new OnboardingQuestionSource(harness.Questions,
            () => [OnboardingConsoleFixture.Describe(project)], fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, source, harness.Liveness, new Epics(), fixture.Clock);
        var stale = Assert.Single((await builder.ReadDecisionsAsync(Token)).Decisions.Where(item => item.FullText.Contains("Stale answer:")));
        Assert.True(stale.CanAnswer);
        Assert.Contains("Recorded answer: npm test | tee", (await source.ReadAsync(Token)).Live
            .Single(item => !item.Text.Contains("Stale answer:")).Text);
        var before = OnboardingConsoleFixture.Snapshot(project);
        var dialogs = new Dialogs { Answer = null };
        using var tracking = new AnswerIntentStatusTracker(harness.Answers.ReadStatusAsync, fixture.Clock);
        var workflow = new OwnerDecisionWorkflow(source, harness.Answers, dialogs, tracking, null, fixture.Clock);
        await workflow.AnswerAsync(stale, false, null, Token);
        Assert.Equal(before.OrderBy(pair => pair.Key), OnboardingConsoleFixture.Snapshot(project).OrderBy(pair => pair.Key));
        dialogs.Answer = "npm test";
        await workflow.AnswerAsync(stale, false, null, Token);
        Assert.Equal("npm test", Assert.Single(OwnerAnswerFile.Read(project.OrchestratorDirectory)).Value);
        Assert.Empty((await source.ReadAsync(Token)).Live);
    }

    [Fact]
    public async Task ProjectFilter_AndSameFactKey_DoNotRouteAnswersAcrossProjects_OrAlterGoalItems()
    {
        using var fixture = new OnboardingConsoleFixture();
        var first = fixture.Register("first");
        var second = fixture.Register("second");
        foreach (var project in new[] { first, second })
            OnboardingConsoleFixture.Store(project, OnboardingConsoleFixture.Model(OnboardingConsoleFixture.TestKey));
        var goal = new OwnerQuestion("goal-question", "12345678-goal", OwnerQuestionKind.Clarification, "Ship?");
        var hidden = new HiddenOwnerQuestion(goal with { ItemId = "hidden" }, "held");
        var raised = new Dictionary<string, DateTimeOffset> { [goal.ItemId] = fixture.Clock.GetUtcNow() };
        var inner = new Questions(new([goal], [hidden], raised));
        IReadOnlyList<OwnerConsoleBoardProject> scope = [OnboardingConsoleFixture.Describe(first), OnboardingConsoleFixture.Describe(second)];
        var source = new OnboardingQuestionSource(inner, () => scope, fixture.Clock);
        var snapshot = await source.ReadAsync(Token);
        Assert.Equal(goal, snapshot.Live[0]);
        Assert.Same(inner.Snapshot.Hidden, snapshot.Hidden);
        Assert.Same(raised, snapshot.RaisedAt);
        Assert.Equal(3, snapshot.Live.Select(item => item.ItemId).Distinct().Count());
        scope = [OnboardingConsoleFixture.Describe(second)];
        var item = Assert.Single((await source.ReadAsync(Token)).Live.Where(item => item.Kind == OwnerQuestionKind.Onboarding));
        Assert.Contains("Project: second", item.Text);
        Assert.True((await source.AnswerAsync(item.ItemId, "npm test", Token)).Success);
        Assert.Empty(OwnerAnswerFile.Read(first.OrchestratorDirectory));
        Assert.Single(OwnerAnswerFile.Read(second.OrchestratorDirectory));
        Assert.Equal(goal, Assert.Single((await source.ReadAsync(Token)).Live));
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }
    private sealed class Questions(OwnerQuestionSnapshot snapshot) : IOwnerQuestionSource
    {
        internal OwnerQuestionSnapshot Snapshot => snapshot;
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot.Live);
        public Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal string? Answer;
        internal readonly List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(true);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult(Answer);
    }
}
