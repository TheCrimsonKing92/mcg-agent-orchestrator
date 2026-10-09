using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each session owns its stores and uninitialized application; gates drive refreshes.
public sealed class OwnerConsoleDecisionDetailTests
{
    [Fact(Timeout = 30_000)]
    public async Task DetailAnswerUsesBoundQuestionAndShowsQueuedNotice()
    {
        await using var session = new Session();
        session.AddQuestion("q1", "Ship?\nFull context", "ship");
        session.AddQuestion("q2", "Which branch?");
        await session.OpenAsync();
        var dialog = session.Dialog;
        Assert.Equal(["Answer (r)", "Accept default (a)", "Close"], dialog.Actions);
        Assert.Contains("r answer", dialog.HintText);
        Assert.Contains("a accept default", dialog.HintText);
        Assert.Contains("Full context", string.Join("\n", dialog.Lines));
        session.Controller.SelectIndex(1); // The modal answers its own row, even after selection changes.

        await dialog.HandleKeyAsync(new Key('r'));

        Assert.Equal(("q1", "owner answer"), Assert.Single(session.Harness.Answers.Calls));
        var queued = AnswerIntentStatusTracker.Queued(1, "11111111");
        Assert.Equal(queued, dialog.NoticeText);
        Assert.Equal(queued, Assert.Single(session.View.Notices));
        Assert.Equal(1, session.Dialogs.PromptCalls);
        Assert.False(session.OpenTask.IsCompleted);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetailAcceptDefaultUsesExistingConfirmation(bool confirmed)
    {
        await using var session = new Session();
        session.AddQuestion("q1", "Ship?", "ship");
        session.Dialogs.Confirmed = confirmed;
        await session.OpenAsync();

        await session.Dialog.HandleKeyAsync(new Key('a'));

        Assert.Equal(1, session.Dialogs.ConfirmCalls);
        Assert.Equal("ship", session.Dialogs.ConfirmationText);
        Assert.Equal(confirmed ? [("q1", "ship")] : Array.Empty<(string, string)>(), session.Harness.Answers.Calls);
        Assert.Equal(confirmed ? AnswerIntentStatusTracker.Queued(1, "11111111") : "", session.Dialog.NoticeText);
        Assert.False(session.OpenTask.IsCompleted);
    }

    [Fact(Timeout = 30_000)]
    public async Task LiveRowRefreshUpdatesBodyActionsAndContextHelp()
    {
        await using var session = new Session();
        session.AddQuestion("q1", "Ship?");
        await session.OpenAsync();
        Assert.Equal(["Answer (r)", "Close"], session.Dialog.Actions);
        Assert.DoesNotContain("a accept default", session.Dialog.HintText);
        await session.Dialog.HandleKeyAsync(new Key('a'));
        Assert.Equal(0, session.Dialogs.ConfirmCalls);
        await session.Dialog.HandleKeyAsync(new Key('?'));
        Assert.DoesNotContain("a:", session.Dialogs.Texts.Last().Text);

        session.Harness.Questions.Items[0] = session.Harness.Questions.Items[0] with
        { Text = "Updated question", ProposedDefault = "wait" };
        await session.RefreshAsync();

        Assert.Contains("Updated question", string.Join("\n", session.Dialog.Lines));
        Assert.Contains("default: wait", string.Join("\n", session.Dialog.Lines));
        Assert.Contains("Accept default (a)", session.Dialog.Actions);
        await session.Dialog.HandleKeyAsync(new Key('?'));
        Assert.Contains("a: Accept", session.Dialogs.Texts.Last().Text);
        session.Harness.Questions.Items[0] = session.Harness.Questions.Items[0] with { ProposedDefault = null };
        await session.RefreshAsync();
        Assert.DoesNotContain("Accept default (a)", session.Dialog.Actions);
        Assert.DoesNotContain("a accept default", session.Dialog.HintText);
    }

    [Fact(Timeout = 30_000)]
    public async Task RefreshShowsRecordedAuthorAnswerAndRemovesAnswerActions()
    {
        await using var session = new Session();
        var store = new CollaborationItemStore(Path.Combine(session.Scene.DirectoryPath, "collaboration-items.db"));
        var item = await store.RaiseAsync(CollaborationItemType.Clarification, session.Scene.Goal.Id.Value,
            "Direction", "Question: Which direction?", "spec-clarification:live");
        session.AddQuestion(item.Id, "Which direction?", "left", OwnerQuestionKind.Clarification);
        await session.OpenAsync();
        Assert.Contains("Answer (r)", session.Dialog.Actions);

        await store.TryResolveAsync(item.CorrelationKey!, "Closed by answer");
        await session.Scene.RecordAnswer(item.Id, "Use the documented direction.\nKeep the complete answer.",
            OwnerQuestionResolutionDialogTests.Scene.At);
        session.Harness.Questions.Items.Clear();
        await session.RefreshAsync();

        Assert.Equal("Resolved: answered by the Author at " +
            OwnerQuestionResolutionDialogTests.Scene.Local(OwnerQuestionResolutionDialogTests.Scene.At), session.Dialog.BannerText);
        Assert.Contains("Use the documented direction.", string.Join("\n", session.Dialog.Lines));
        Assert.Contains("Keep the complete answer.", string.Join("\n", session.Dialog.Lines));
        await AssertClosedToAnswers(session);
        Assert.False(session.OpenTask.IsCompleted);
    }

    [Fact(Timeout = 30_000)]
    public async Task RefreshShowsDismissedQuestionAndRemovesAnswerActions()
    {
        await using var session = new Session();
        var request = session.Harness.Kernel.RequestHumanInput(session.Scene.Goal.Id, session.Scene.Goal.Tasks[0].Id,
            "Should we wait?", isDismissible: true);
        session.AddQuestion(request.Id.Value, request.Question, "wait");
        await session.OpenAsync();

        session.Harness.Kernel.DismissHumanInput(request.Id);
        session.Harness.Questions.Items.Clear();
        await session.RefreshAsync();

        Assert.StartsWith("Resolved: dismissed at ", session.Dialog.BannerText);
        await AssertClosedToAnswers(session);
    }

    [Fact(Timeout = 30_000)]
    public async Task RefreshShowsSupersededQuestionAndRemovesAnswerActions()
    {
        await using var session = new Session();
        var store = new CollaborationItemStore(Path.Combine(session.Scene.DirectoryPath, "collaboration-items.db"));
        var item = await store.RaiseAsync(CollaborationItemType.Clarification, session.Scene.Goal.Id.Value,
            "Old direction", "Question: Which direction?", "spec-clarification:old");
        session.AddQuestion(item.Id, "Which direction?", "left", OwnerQuestionKind.Clarification);
        await session.OpenAsync();
        await store.TryResolveAsync(item.CorrelationKey!, "Superseded by a replacement question");
        session.Harness.Questions.Items.Clear();
        await session.RefreshAsync();
        Assert.StartsWith("Resolved: superseded at ", session.Dialog.BannerText);
        await AssertClosedToAnswers(session);
    }

    [Fact(Timeout = 30_000)]
    public async Task VanishedQuestionWithoutResolutionIsNoLongerOpen()
    {
        await using var session = new Session();
        session.AddQuestion("q1", "Ship?", "ship");
        await session.OpenAsync();
        session.Harness.Questions.Items.Clear();
        await session.RefreshAsync();
        Assert.Equal("No longer open", session.Dialog.BannerText);
        await AssertClosedToAnswers(session);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, "question 1 is no longer open")]
    [InlineData(true, "Question changed; review it again before answering.")]
    public async Task DetailAnswerRetainsStaleQuestionChecks(bool changed, string expected)
    {
        await using var session = new Session();
        session.AddQuestion("q1", "Ship?", "ship");
        session.Dialogs.OnPrompt = () =>
        {
            if (changed) session.Harness.Questions.Items[0] = session.Harness.Questions.Items[0] with { Text = "New question" };
            else session.Harness.Questions.Items.Clear();
        };
        await session.OpenAsync();
        await session.Dialog.HandleKeyAsync(new Key('r'));
        Assert.Empty(session.Harness.Answers.Calls);
        Assert.Equal(expected, session.Dialog.NoticeText);
        Assert.Equal(expected, Assert.Single(session.Dialogs.Texts).Text);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(OperatorIntentStatus.Applied)]
    [InlineData(OperatorIntentStatus.Rejected)]
    public async Task AppliedAndRejectedNoticesRemainVisibleInsideDetail(OperatorIntentStatus status)
    {
        var answers = new TrackedAnswers(status);
        await using var session = new Session(answers);
        session.AddQuestion("q1", "Ship?", "ship");
        await session.OpenAsync();
        await session.Dialog.HandleKeyAsync(new Key('r'));
        Assert.Equal(AnswerIntentStatusTracker.Queued(1, "11111111"), session.Dialog.NoticeText);
        answers.Release.TrySetResult();
        await session.Tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);
        var expected = status == OperatorIntentStatus.Applied ? AnswerIntentStatusTracker.Applied(1, "11111111") :
            AnswerIntentStatusTracker.Rejected(1, "11111111", "duplicate answer");
        Assert.Equal(expected, session.Dialog.NoticeText);
        Assert.Contains(expected, session.View.Notices);
        Assert.False(session.OpenTask.IsCompleted);
    }

    [Fact(Timeout = 30_000)]
    public async Task HeldResolutionReadDoesNotHoldInputAndDisposedSessionIgnoresResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var decision = new OwnerConsoleDecision("q1", 1, "goal", "goal", OwnerQuestionKind.HumanInput,
            "Ship?", "Ship?", null, null, "ship");
        using var detail = new OwnerConsoleDecisionDetail(decision, TimeZoneInfo.Utc,
            (_, _, _) => Task.CompletedTask, async token =>
            {
                Interlocked.Increment(ref reads);
                entered.TrySetResult();
                await release.Task; // Intentionally ignores cancellation to test the late-result guard.
                return null;
            }, _ => Task.CompletedTask);
        using var dialog = new OwnerConsoleDecisionDialog(detail, action => action());
        var closed = false;
        dialog.Closed += () => closed = true;
        detail.Observe(null);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("reading how it was resolved", dialog.BannerText);
            Assert.Equal(["Close"], dialog.Actions);
            detail.Observe(null); // Coalesce a refresh while the read is held.
            Assert.Equal(1, reads);
            await dialog.HandleKeyAsync(Key.Esc);
            Assert.True(closed);
            var banner = dialog.BannerText;
            detail.Dispose();
            await detail.LastRefresh.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(banner, dialog.BannerText);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task AssertClosedToAnswers(Session session)
    {
        Assert.Equal(["Close"], session.Dialog.Actions);
        Assert.DoesNotContain("r answer", session.Dialog.HintText);
        Assert.DoesNotContain("a accept default", session.Dialog.HintText);
        await session.Dialog.HandleKeyAsync(new Key('r'));
        await session.Dialog.HandleKeyAsync(new Key('a'));
        Assert.Equal(0, session.Dialogs.PromptCalls);
        Assert.Equal(0, session.Dialogs.ConfirmCalls);
        Assert.Empty(session.Harness.Answers.Calls);
        await session.Dialog.HandleKeyAsync(new Key('?'));
        Assert.DoesNotContain("r:", session.Dialogs.Texts.Last().Text);
        Assert.DoesNotContain("a:", session.Dialogs.Texts.Last().Text);
        Assert.Contains("Esc: Close", session.Dialogs.Texts.Last().Text);
    }

    private sealed class Session : IAsyncDisposable
    {
        internal readonly OwnerQuestionResolutionDialogTests.Scene Scene = new();
        internal OwnerConsoleHarness Harness => Scene.Harness;
        internal readonly LiveDialogs Dialogs = new();
        internal readonly IApplication App = Application.Create();
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly AnswerIntentStatusTracker Tracker;
        internal OwnerConsoleDecisionDialog Dialog => Dialogs.Dialog!;
        internal Task OpenTask = Task.CompletedTask;
        internal Session(IOwnerAnswerSubmitter? answers = null)
        {
            answers ??= Harness.Answers;
            Tracker = new(answers.ReadStatusAsync, Harness.Clock);
            Controller = new(Harness.Questions, answers, Dialogs, Harness.State, Scene.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock, answerTracking: Tracker, resolutions: Scene.Reader);
            View = new(App, Controller, () => Task.CompletedTask);
        }
        internal void AddQuestion(string id, string text, string? proposedDefault = null,
            OwnerQuestionKind kind = OwnerQuestionKind.HumanInput) =>
            Harness.Questions.Items.Add(new(id, Scene.Goal.Id.Value, kind, text, "repo", "high", proposedDefault));
        internal async Task OpenAsync()
        {
            await RefreshAsync();
            View.FocusDecisions();
            OpenTask = View.HandleKeyAsync(Key.Enter);
            await Dialogs.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        internal async Task RefreshAsync()
        {
            var model = await new OwnerConsoleViewModelBuilder(Harness.State, Harness.Questions,
                Harness.Liveness, new Epics(), Harness.Clock).BuildAsync(new(Harness.Clock.GetUtcNow(), null, [], 0));
            View.Render(model);
            if (Dialogs.Dialog is { } dialog) await dialog.LastRefresh.WaitAsync(TestContext.Current.CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            Dialogs.Close.TrySetResult();
            await OpenTask;
            View.Dispose(); Controller.Dispose(); App.Dispose(); Scene.Dispose();
        }
    }

    private sealed class LiveDialogs : IOwnerConsoleDialogs
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Close = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnerConsoleDecisionDialog? Dialog;
        internal int PromptCalls;
        internal int ConfirmCalls;
        internal bool Confirmed = true;
        internal string? ConfirmationText;
        internal Action? OnPrompt;
        internal readonly List<(string Title, string Text)> Texts = [];
        public async Task ShowDecisionAsync(OwnerConsoleDecisionDetail detail)
        {
            using var dialog = Dialog = new(detail, action => action());
            dialog.Closed += () => Close.TrySetResult();
            Entered.TrySetResult();
            await Close.Task;
        }
        public Task<string?> PromptTextAsync(string title, string text)
        { PromptCalls++; OnPrompt?.Invoke(); return Task.FromResult<string?>("owner answer"); }
        public Task<bool> ConfirmAsync(string title, string text)
        { ConfirmCalls++; ConfirmationText = text; return Task.FromResult(Confirmed); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }

    private sealed class TrackedAnswers(OperatorIntentStatus status) : IOwnerAnswerSubmitter
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OwnerAnswerSubmission Submit(OwnerQuestion question, string answer) => new("intent-1");
        public async Task<OwnerAnswerIntentStatus?> ReadStatusAsync(string intentId, CancellationToken token)
        { await Release.Task.WaitAsync(token); return new(status, "duplicate answer"); }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
