using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test fakes and an uninitialized application; no global terminal or store.
public sealed class OwnerConsoleAnswerTrackingTests
{
    private const string Queued = "Answer queued for question 1 (11111111); the conductor applies it on its next tick.";
    private const string Applied = "Answer applied for question 1 (11111111)";
    private const string StillQueued = "Answer still queued for question 1; the conductor has not applied it yet";

    [Theory(Timeout = 30_000)] // Hang detector only; outcomes use scripted reads and virtual time.
    [InlineData("applied", Applied, 3)]
    [InlineData("rejected", "Answer rejected for question 1 (11111111): duplicate answer", 1)]
    [InlineData("rejected-no-outcome", "Answer rejected for question 1 (11111111)", 1)]
    [InlineData("pending", StillQueued, 3)]
    public async Task FullScreen_SubmittedAnswer_QueuesThenReportsIntentOutcome(
        string scenario, string expected, int reads)
    {
        var harness = Harness();
        var clock = new SteppingClock();
        var answers = new ScriptedAnswers(scenario);
        using var tracker = Tracker(answers, clock);
        var dialogs = new Dialogs();
        using var controller = new OwnerConsoleScreenController(harness.Questions, answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest,
            clock, answerTracking: tracker);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, clock: clock);
        view.Render(await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), clock).BuildAsync(new(clock.GetUtcNow(), null, [], 0)));

        await view.HandleKeyAsync(new Key('r'));

        Assert.Equal(Queued, Assert.Single(view.Notices));
        Assert.Empty(dialogs.Texts); // Queue acknowledgement never opens a modal result dialog.
        Assert.Equal(("q1", "yes"), Assert.Single(answers.Submissions));
        Assert.Single(harness.Questions.Items); // No optimistic mutation of conductor state.
        answers.ReleaseStatusRead.TrySetResult();
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal([expected, Queued], view.Notices);
        Assert.Equal(reads, answers.Reads);
        Assert.All(answers.StatusIds, id => Assert.Equal("intent-1", id));
        Assert.All(view.Notices, text => Assert.DoesNotContain("not accepted", text));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("applied", Applied, 3)]
    [InlineData("rejected", "Answer rejected for question 1 (11111111): duplicate answer", 1)]
    [InlineData("pending", StillQueued, 3)]
    public async Task PlainMode_SubmittedAnswer_QueuesThenReportsIntentOutcome(
        string scenario, string expected, int reads)
    {
        var harness = Harness();
        var clock = new SteppingClock();
        var answers = new ScriptedAnswers(scenario);
        using var tracker = Tracker(answers, clock);
        using var session = new OwnerConsoleSession(harness.State, harness.Questions, answers,
            harness.Liveness, harness.Digest, harness.Tail, harness.Output, clock, answerTracking: tracker);

        await session.HandleCommandAsync("answer 1 yes", TestContext.Current.CancellationToken);

        Assert.Contains(Queued, harness.Output.Text);
        Assert.DoesNotContain(expected, harness.Output.Text);
        Assert.Equal(("q1", "yes"), Assert.Single(answers.Submissions));
        answers.ReleaseStatusRead.TrySetResult();
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        var text = harness.Output.Text;
        Assert.Contains(expected, text);
        Assert.True(text.IndexOf(Queued, StringComparison.Ordinal) < text.IndexOf(expected, StringComparison.Ordinal));
        Assert.DoesNotContain("not accepted", text);
        Assert.Equal(reads, answers.Reads);
        Assert.All(answers.StatusIds, id => Assert.Equal("intent-1", id));
    }

    [Fact(Timeout = 30_000)]
    public async Task PlainMode_DisposedSession_CancelsTrackingWithoutLateNotices()
    {
        var harness = Harness();
        var clock = new SteppingClock();
        var answers = new ScriptedAnswers("applied");
        using var tracker = Tracker(answers, clock);
        using var session = new OwnerConsoleSession(harness.State, harness.Questions, answers,
            harness.Liveness, harness.Digest, harness.Tail, harness.Output, clock, answerTracking: tracker);
        await session.HandleCommandAsync("answer 1 yes", TestContext.Current.CancellationToken);
        await answers.StatusReadEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queuedOutput = harness.Output.Text;

        session.Dispose();
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(Queued, queuedOutput);
        Assert.Equal(queuedOutput, harness.Output.Text);
        Assert.Equal(1, answers.Reads);
    }

    [Fact(Timeout = 30_000)]
    public async Task Tracker_ConcurrentAnswers_TracksEveryReceiptIndependently()
    {
        var clock = new SteppingClock();
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var tracker = new AnswerIntentStatusTracker(async (id, token) =>
        {
            if (Interlocked.Increment(ref readCount) == 2) readEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(OperatorIntentStatus.Applied);
        }, clock);
        tracker.Track("intent-1", 1, "11111111", messages.Enqueue);
        tracker.Track("intent-2", 2, "22222222", messages.Enqueue);
        await readEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
        Assert.Contains(Applied, messages);
        Assert.Contains("Answer applied for question 2 (22222222)", messages);
    }

    [Fact(Timeout = 30_000)]
    public async Task Tracker_FailedReadAndClaimedIntent_ContinuesToApplied()
    {
        var clock = new SteppingClock();
        var count = 0;
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var tracker = new AnswerIntentStatusTracker((id, token) =>
        {
            count++;
            if (count == 1) throw new IOException("status unavailable");
            return Task.FromResult<OwnerAnswerIntentStatus?>(new(count == 2
                ? OperatorIntentStatus.Claimed : OperatorIntentStatus.Applied));
        }, clock, delay: clock.Delay);

        tracker.Track("intent-1", 1, "11111111", messages.Enqueue);
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Applied, Assert.Single(messages));
        Assert.Equal(3, count);
    }

    [Fact(Timeout = 30_000)]
    public async Task Tracker_HeldStatusRead_ExpiresWithStillQueuedAndCancelsRead()
    {
        var clock = new SteppingClock();
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var tracker = new AnswerIntentStatusTracker(async (id, token) =>
        {
            readEntered.TrySetResult();
            try { await hold.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { readCancelled.TrySetResult(); throw; }
            return new(OperatorIntentStatus.Applied);
        }, clock, bound: TimeSpan.FromSeconds(3));
        tracker.Track("intent-1", 1, "11111111", messages.Enqueue);
        await readEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await clock.TimerCreated.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(3));
        await tracker.WhenIdle().WaitAsync(TestContext.Current.CancellationToken);
        await readCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StillQueued, Assert.Single(messages));
    }

    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.Clarification, "Ship?"));
        return harness;
    }

    private static AnswerIntentStatusTracker Tracker(ScriptedAnswers answers, SteppingClock clock) =>
        new(answers.ReadStatusAsync, clock, bound: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromSeconds(1), delay: clock.Delay);

    private sealed class SteppingClock : TimeProvider
    {
        private readonly ManualStewardTimeProvider _timers = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal Task TimerCreated => _timers.TimerCreated.Task;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal Task Delay(TimeSpan interval, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(interval);
            return Task.CompletedTask;
        }
        internal void Advance(TimeSpan interval)
        {
            _now += interval;
            _timers.Advance(interval);
        }
    }

    private sealed class ScriptedAnswers(string scenario) : IOwnerAnswerSubmitter
    {
        internal readonly TaskCompletionSource ReleaseStatusRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StatusReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(string Id, string Text)> Submissions = [];
        internal readonly List<string> StatusIds = [];
        internal int Reads;
        public OwnerAnswerSubmission Submit(OwnerQuestion question, string answer)
        { Submissions.Add((question.ItemId, answer)); return new("intent-1"); }
        public async Task<OwnerAnswerIntentStatus?> ReadStatusAsync(string intentId, CancellationToken token)
        {
            StatusIds.Add(intentId);
            Reads++;
            StatusReadEntered.TrySetResult();
            await ReleaseStatusRead.Task.WaitAsync(token);
            return scenario switch
            {
                "applied" when Reads >= 3 => new(OperatorIntentStatus.Applied),
                "rejected" => new(OperatorIntentStatus.Rejected, "duplicate answer"),
                "rejected-no-outcome" => new(OperatorIntentStatus.Rejected),
                _ => new(OperatorIntentStatus.Pending)
            };
        }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>("yes");
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(true);
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
