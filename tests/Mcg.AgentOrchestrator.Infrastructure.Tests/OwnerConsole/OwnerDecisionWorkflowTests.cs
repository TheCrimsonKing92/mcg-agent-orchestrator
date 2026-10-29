using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its fakes, tracker and notices; no terminal or stores.
public sealed class OwnerDecisionWorkflowTests
{
    [Fact]
    public async Task AnswerAsync_ViewOnlyKind_ShowsViewOnlyNotice()
    {
        using var fixture = new Fixture();
        var decision = fixture.Decision with { Kind = OwnerQuestionKind.StewardHold };

        await fixture.AnswerAsync(decision);

        fixture.AssertNotice("View only",
            "Steward questions are answered through goal verbs for now; use the CLI retry/adjudicate commands for goal 11111111.");
        Assert.Empty(fixture.Dialogs.Prompts);
    }

    [Fact]
    public async Task AnswerAsync_AcceptWithoutDefault_ShowsNoDefaultNotice()
    {
        using var fixture = new Fixture();

        await fixture.AnswerAsync(fixture.Decision with { ProposedDefault = null }, accept: true);

        fixture.AssertNotice("Decision", "question 1 has no proposed default");
        Assert.Empty(fixture.Dialogs.Confirmations);
    }

    [Fact]
    public async Task AnswerAsync_DeclinedConfirmation_SubmitsNothing()
    {
        using var fixture = new Fixture();
        fixture.Dialogs.Confirm = false;

        await fixture.AnswerAsync(fixture.Decision, accept: true);

        Assert.Equal(("Accept default?", "yes"), Assert.Single(fixture.Dialogs.Confirmations));
        Assert.Empty(fixture.Harness.Answers.Calls);
        Assert.Empty(fixture.Dialogs.Texts);
        Assert.Empty(fixture.Dialogs.Prompts);
    }

    [Fact]
    public async Task AnswerAsync_EmptyAnswer_ShowsValidationNotice()
    {
        using var fixture = new Fixture();
        fixture.Dialogs.Answer = "";

        await fixture.AnswerAsync(fixture.Decision);

        fixture.AssertNotice("Answer", "Answer cannot be empty or start with --");
        Assert.Equal(("Answer", "Ship?"), Assert.Single(fixture.Dialogs.Prompts));
    }

    [Fact]
    public async Task AnswerAsync_AnswerStartingWithDoubleDash_ShowsValidationNotice()
    {
        using var fixture = new Fixture();
        fixture.Dialogs.Answer = "--yes";

        await fixture.AnswerAsync(fixture.Decision);

        fixture.AssertNotice("Answer", "Answer cannot be empty or start with --");
        Assert.Equal(("Answer", "Ship?"), Assert.Single(fixture.Dialogs.Prompts));
    }

    [Fact]
    public async Task AnswerAsync_QuestionNoLongerOpen_ShowsNoLongerOpenNotice()
    {
        using var fixture = new Fixture();
        fixture.Harness.Questions.Items.Clear();

        await fixture.AnswerAsync(fixture.Decision);

        fixture.AssertNotice("Decision", "question 1 is no longer open");
    }

    [Fact]
    public async Task AnswerAsync_QuestionChangedSinceDisplay_ShowsChangedNotice()
    {
        using var fixture = new Fixture();
        fixture.Harness.Questions.Items[0] = (fixture.Decision with { FullText = "Ship tomorrow?" }).ToQuestion();

        await fixture.AnswerAsync(fixture.Decision);

        fixture.AssertNotice("Decision", "Question changed; review it again before answering.");
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Hang detector; completion uses tracker.WhenIdle.
    public async Task AnswerAsync_QueuedSubmission_IsTracked()
    {
        var harness = new OwnerConsoleHarness();
        var decision = Decision();
        harness.Questions.Items.Add(decision.ToQuestion());
        var answers = new QueuedAnswers();
        var dialogs = new Dialogs { Answer = "ship it" };
        var notices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var detailNotices = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var failures = new List<string>();
        using var tracker = new AnswerIntentStatusTracker(answers.ReadStatusAsync, harness.Clock);
        var workflow = new OwnerDecisionWorkflow(harness.Questions, answers, dialogs, tracker, null, harness.Clock);
        var operation = new OwnerConsoleScreenOperation(harness.Clock, _ => { }, failures.Add, notify: notices.Enqueue);

        await workflow.AnswerAsync(decision, false, operation, TestContext.Current.CancellationToken, detailNotices.Enqueue);
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("q1", "ship it"), Assert.Single(answers.Calls));
        Assert.Equal("intent-1", Assert.Single(answers.StatusIds));
        string[] expected =
        [
            "Answer queued for question 1 (11111111); the conductor applies it on its next tick.",
            "Answer applied for question 1 (11111111)"
        ];
        Assert.Equal(expected, notices.ToArray());
        Assert.Equal(expected, detailNotices.ToArray());
        Assert.Empty(dialogs.Texts);
        Assert.Empty(failures);
        Assert.Equal(decision.ToQuestion(), Assert.Single(harness.Questions.Items));
    }

    private static OwnerConsoleDecision Decision() =>
        new("q1", 1, "11111111", "11111111", OwnerQuestionKind.Clarification, "Ship?", "Ship?", null, null, "yes");

    private sealed class Fixture : IDisposable
    {
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly Dialogs Dialogs = new();
        internal readonly OwnerConsoleDecision Decision = OwnerDecisionWorkflowTests.Decision();
        private readonly AnswerIntentStatusTracker _tracker;
        private readonly OwnerDecisionWorkflow _workflow;

        internal Fixture()
        {
            Harness.Questions.Items.Add(Decision.ToQuestion());
            _tracker = new(Harness.Answers.ReadStatusAsync, Harness.Clock);
            _workflow = new(Harness.Questions, Harness.Answers, Dialogs, _tracker, null, Harness.Clock);
        }

        internal Task AnswerAsync(OwnerConsoleDecision decision, bool accept = false) =>
            _workflow.AnswerAsync(decision, accept, null, TestContext.Current.CancellationToken);

        internal void AssertNotice(string title, string text)
        {
            Assert.Equal((title, text), Assert.Single(Dialogs.Texts));
            Assert.Empty(Harness.Answers.Calls);
        }

        public void Dispose() => _tracker.Dispose();
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal string? Answer = "yes";
        internal bool Confirm = true;
        internal readonly List<(string Title, string Text)> Texts = [];
        internal readonly List<(string Title, string Text)> Prompts = [];
        internal readonly List<(string Title, string Text)> Confirmations = [];
        public Task<string?> PromptTextAsync(string title, string text)
        { Prompts.Add((title, text)); return Task.FromResult(Answer); }
        public Task<bool> ConfirmAsync(string title, string text)
        { Confirmations.Add((title, text)); return Task.FromResult(Confirm); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }

    private sealed class QueuedAnswers : IOwnerAnswerSubmitter
    {
        internal readonly List<(string Id, string Text)> Calls = [];
        internal readonly List<string> StatusIds = [];
        public OwnerAnswerSubmission Submit(OwnerQuestion question, string answer)
        { Calls.Add((question.ItemId, answer)); return new("intent-1"); }
        public Task<OwnerAnswerIntentStatus?> ReadStatusAsync(string intentId, CancellationToken cancellationToken)
        {
            StatusIds.Add(intentId);
            return Task.FromResult<OwnerAnswerIntentStatus?>(new(OperatorIntentStatus.Applied));
        }
    }
}
