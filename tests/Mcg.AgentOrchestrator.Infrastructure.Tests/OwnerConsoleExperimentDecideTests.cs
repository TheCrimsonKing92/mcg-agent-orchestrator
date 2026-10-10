using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each case owns its fakes, clock and uninitialized terminal application.
public sealed class OwnerConsoleExperimentDecideTests
{
    private const string Queued = "Answer queued for question 1 (experiment 1a2b3c4d); the conductor applies it on its next tick.";
    private const string Applied = "Answer applied for question 1 (experiment 1a2b3c4d)";

    [Fact(Timeout = 30_000)] // Hang-only bound; status delivery is controlled by an event gate.
    public async Task OwnerExperimentDecisionFlow_ValidForm_QueuesThenReportsApplied()
    {
        var form = new OwnerExperimentDecisionForm("confirmed", " receipt:chosen \n", " Keep this result \n");
        using var scene = await Scene.CreateAsync(form);
        await scene.View.HandleKeyAsync(new Key('r'));

        Assert.Equal("experiment-show:1a2b3c4d", Assert.Single(scene.Dialogs.Defaults));
        Assert.Equal(("1a2b3c4d", form), Assert.Single(scene.Answers.Decisions));
        Assert.Empty(scene.Answers.TextAnswers);
        Assert.Equal(0, scene.Dialogs.PromptCalls);
        Assert.Equal(0, scene.Dialogs.ConfirmCalls);
        Assert.Equal(Queued, Assert.Single(scene.View.NoticeStrip.Visible).Text);
        Assert.Empty(scene.Dialogs.Texts);

        scene.Answers.ReleaseStatus.TrySetResult();
        await scene.Tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { Applied, Queued }, scene.View.NoticeStrip.Visible.Select(entry => entry.Text));
        Assert.Equal(OwnerConsoleNoticeSeverity.Success, scene.View.NoticeStrip.Visible[0].Severity);
        Assert.Equal("intent-experiment", Assert.Single(scene.Answers.StatusIds));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("confirmed", "receipt", " \n ", "action is required.")]
    [InlineData("confirmed", " \n ", "Keep", "evidence is required.")]
    [InlineData("keep", "receipt", "Keep", "outcome: expected confirmed, refuted or inconclusive.")]
    public async Task OwnerExperimentDecisionFlow_InvalidForm_RefusesBeforeSubmit(string outcome, string evidence, string action, string refusal)
    {
        using var scene = await Scene.CreateAsync(new OwnerExperimentDecisionForm(outcome, evidence, action));
        await scene.View.HandleKeyAsync(new Key('r'));
        var notice = Assert.Single(scene.View.NoticeStrip.Visible);
        Assert.Equal(refusal, notice.Text);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, notice.Severity);
        Assert.Empty(scene.Answers.Decisions);
        Assert.Empty(scene.Answers.TextAnswers);
        Assert.Empty(scene.Answers.StatusIds);
        Assert.Single(scene.Dialogs.Defaults);
    }

    [Fact(Timeout = 30_000)]
    public async Task OwnerExperimentDecisionFlow_CancelledDialog_DoesNotSubmit()
    {
        using var scene = await Scene.CreateAsync(null);
        await scene.View.HandleKeyAsync(new Key('r'));
        Assert.Single(scene.Dialogs.Defaults);
        Assert.Empty(scene.Answers.Decisions);
        Assert.Empty(scene.View.NoticeStrip.Visible);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("gone")]
    [InlineData("changed")]
    [InlineData("malformed")]
    public async Task OwnerExperimentDecisionFlow_StaleReading_RefusesBeforeDialog(string scenario)
    {
        using var scene = await Scene.CreateAsync(new OwnerExperimentDecisionForm("confirmed", "receipt", "Keep"),
            malformed: scenario == "malformed");
        if (scenario == "gone") scene.Harness.Questions.Items.Clear();
        if (scenario == "changed") scene.Harness.Questions.Items[0] = scene.Harness.Questions.Items[0] with { Text = "changed" };
        await scene.View.HandleKeyAsync(new Key('r'));
        Assert.Equal("question 1 is no longer open", Assert.Single(scene.View.NoticeStrip.Visible).Text);
        Assert.Empty(scene.Dialogs.Defaults);
        Assert.Empty(scene.Answers.Decisions);
    }

    [Fact(Timeout = 30_000)]
    public async Task OwnerExperimentDecisionFlow_ChangedDuringDialog_RefusesSubmit()
    {
        using var scene = await Scene.CreateAsync(new OwnerExperimentDecisionForm("confirmed", "receipt", "Keep"));
        scene.Dialogs.OnPrompt = () => scene.Harness.Questions.Items.Clear();
        await scene.View.HandleKeyAsync(new Key('r'));
        Assert.Single(scene.Dialogs.Defaults);
        Assert.Equal("question 1 is no longer open", Assert.Single(scene.View.NoticeStrip.Visible).Text);
        Assert.Empty(scene.Answers.Decisions);
    }

    [Fact(Timeout = 30_000)]
    public async Task OwnerExperimentDecisionFlow_DecidedExperiment_ShowsRejection()
    {
        using var scene = await Scene.CreateAsync(new OwnerExperimentDecisionForm("confirmed", "receipt", "Keep"));
        scene.Answers.Status = new(OperatorIntentStatus.Rejected, "Rejected experiment-decide: experiment-decided");
        await scene.View.HandleKeyAsync(new Key('r'));
        Assert.Equal(Queued, Assert.Single(scene.View.NoticeStrip.Visible).Text);
        scene.Answers.ReleaseStatus.TrySetResult();
        await scene.Tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        var rejected = scene.View.NoticeStrip.Visible[0];
        Assert.Equal("Answer rejected for question 1 (experiment 1a2b3c4d): Rejected experiment-decide: experiment-decided", rejected.Text);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, rejected.Severity);
        Assert.DoesNotContain(scene.View.NoticeStrip.Visible, entry => entry.Text.Contains("Answer applied", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerExperimentDecisionFlow_TextAnswer_RemainsRefused()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid().ToString("n")));
        var error = Assert.Throws<InvalidOperationException>(() =>
        { new AttentionAnswerHandlerAdapter(workspace).Submit(Reading(), "keep"); });
        Assert.Contains("experiment-decide", error.Message);
    }

    [Fact]
    public void DefaultEvidence_FromReading_RoundTripsShortId()
    {
        var reading = Reading();
        Assert.Equal("1a2b3c4d", OwnerExperimentReadingQuestion.ShortId(reading.Text));
        Assert.Equal("experiment-show:1a2b3c4d", OwnerExperimentDecisionForm.DefaultEvidence(
            OwnerExperimentReadingQuestion.ShortId(reading.Text)!));
        Assert.Null(OwnerExperimentReadingQuestion.ShortId("unrelated\n" + reading.Text));
        Assert.Null(OwnerExperimentReadingQuestion.ShortId("Experiment short reading due: stop rule reached"));
    }

    private static OwnerQuestion Reading() => OwnerExperimentReadingQuestion.From(new CollaborationItem(
        "reading", CollaborationItemType.Decision, null, CollaborationItemStatus.Raised, "due", "",
        "experiment-reading-due:1a2b3c4d000000000000000000000000:stop-rule",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null))!;

    private sealed class Scene : IDisposable
    {
        internal OwnerConsoleHarness Harness { get; } = new();
        internal ScriptedAnswers Answers { get; } = new();
        internal Dialogs Dialogs { get; }
        internal AnswerIntentStatusTracker Tracker { get; }
        internal OwnerConsoleFullScreenView View { get; }
        private readonly IApplication _app;
        private readonly OwnerConsoleScreenController _controller;
        private Scene(OwnerExperimentDecisionForm? form, bool malformed)
        {
            Dialogs = new(form);
            var reading = Reading();
            Harness.Questions.Items.Add(malformed ? reading with { Text = "malformed" } : reading);
            Tracker = new(Answers.ReadStatusAsync, Harness.Clock);
            _controller = new(Harness.Questions, Answers, Dialogs, Harness.State, Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock, answerTracking: Tracker);
            _app = Application.Create();
            View = new(_app, _controller, () => Task.CompletedTask, clock: Harness.Clock);
        }
        internal static async Task<Scene> CreateAsync(OwnerExperimentDecisionForm? form, bool malformed = false)
        {
            var scene = new Scene(form, malformed);
            scene.View.Render(await new OwnerConsoleViewModelBuilder(scene.Harness.State, scene.Harness.Questions,
                scene.Harness.Liveness, new Epics(), scene.Harness.Clock).BuildAsync(new(scene.Harness.Clock.GetUtcNow(), null, [], 0)));
            return scene;
        }
        public void Dispose() { View.Dispose(); _controller.Dispose(); Tracker.Dispose(); _app.Dispose(); }
    }

    private sealed class ScriptedAnswers : IOwnerAnswerSubmitter
    {
        internal readonly List<(string Reference, OwnerExperimentDecisionForm Form)> Decisions = [];
        internal readonly List<string> TextAnswers = [];
        internal readonly List<string> StatusIds = [];
        internal readonly TaskCompletionSource ReleaseStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnerAnswerIntentStatus Status = new(OperatorIntentStatus.Applied);
        public OwnerAnswerSubmission Submit(OwnerQuestion question, string answer)
        { TextAnswers.Add(answer); throw new InvalidOperationException("Unexpected text answer"); }
        public OwnerAnswerSubmission SubmitExperimentDecision(string experimentReference, OwnerExperimentDecisionForm form)
        { Decisions.Add((experimentReference, form)); return new("intent-experiment"); }
        public async Task<OwnerAnswerIntentStatus?> ReadStatusAsync(string intentId, CancellationToken token)
        { StatusIds.Add(intentId); await ReleaseStatus.Task.WaitAsync(token); return Status; }
    }

    private sealed class Dialogs(OwnerExperimentDecisionForm? form) : IOwnerConsoleDialogs
    {
        internal readonly List<string> Defaults = [];
        internal readonly List<string> Texts = [];
        internal int PromptCalls;
        internal int ConfirmCalls;
        internal Action? OnPrompt;
        public Task<OwnerExperimentDecisionForm?> PromptExperimentDecisionAsync(string title, string text, string defaultEvidence)
        { Defaults.Add(defaultEvidence); OnPrompt?.Invoke(); return Task.FromResult(form); }
        public Task<string?> PromptTextAsync(string title, string text)
        { PromptCalls++; return Task.FromResult<string?>("yes"); }
        public Task<bool> ConfirmAsync(string title, string text)
        { ConfirmCalls++; return Task.FromResult(true); }
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    internal sealed class DecisionPromptProbe(IOwnerConsoleDialogs inner) : IOwnerConsoleDialogs
    {
        internal readonly List<string> Defaults = [];
        public Task<OwnerExperimentDecisionForm?> PromptExperimentDecisionAsync(string title, string text, string defaultEvidence)
        { Defaults.Add(defaultEvidence); return Task.FromResult<OwnerExperimentDecisionForm?>(null); }
        public Task<string?> PromptTextAsync(string title, string text) => inner.PromptTextAsync(title, text);
        public Task<bool> ConfirmAsync(string title, string text) => inner.ConfirmAsync(title, text);
        public Task ShowTextAsync(string title, string text) => inner.ShowTextAsync(title, text);
        public Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail) => inner.ShowDecisionAsync(detail);
        public Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choices = null) => inner.ShowPageAsync(title, text, choices);
        public Task ShowGoalAsync(OwnerConsoleGoalDialog dialog) => inner.ShowGoalAsync(dialog);
    }
}
