using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;

// Parallel-safe: per-test gates and headless application; no elapsed-time decisions.
public sealed class OwnerConsoleBoardFirstStartupTests
{
    [Fact]
    public async Task BlockedSources_RenderBoardThenFillEachPaneIndependently()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "Board first", AgentRole.Developer);
        var questions = new GatedQuestions();
        var activity = new GatedActivity();
        using var app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness);
        var startup = new OwnerConsoleStartupLoader(Builder(harness, questions), activity, action => action());
        var fill = await startup.StartAsync(view.Render, model =>
        {
            view.Render(model);
            if (model.Decisions.Length > 0) questions.Rendered.TrySetResult();
        }, new(harness.Clock.GetUtcNow(), null, [], 0),
            _ => { }, TestContext.Current.CancellationToken);
        try
        {
            await questions.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await activity.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("11111111", Assert.Single(view.BoardTable.Rows.Cast<System.Data.DataRow>())["GOAL"]);
            Assert.Equal("Board first", view.BoardTable.Rows[0]["TITLE"]);
            Assert.Equal(["loading..."], view.DecisionLines);
            Assert.Equal(["loading..."], view.ActivityLines);
            Assert.Equal(["11111111-first"], activity.GoalIds);

            questions.Release.SetResult([new("q1", "11111111-first", OwnerQuestionKind.HumanInput, "Ship?")]);
            await questions.Rendered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("[1] 11111111 HumanInput: Ship?", Assert.Single(view.DecisionLines));
            Assert.Equal(["loading..."], view.ActivityLines);
        }
        finally
        {
            questions.Release.TrySetResult([]);
            activity.Release.TrySetResult(new([new(DateTimeOffset.UnixEpoch, "acceptance", "11111111-first", "result=passed")], DateTimeOffset.UnixEpoch));
            await fill.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal("[1] 11111111 HumanInput: Ship?", Assert.Single(view.DecisionLines));
        Assert.Contains($"{DateTimeOffset.UnixEpoch.ToLocalTime():HH:mm:ss} 11111111 Board first: passed its tests, landing next", view.ActivityLines);
        Assert.Contains(view.ActivityLines, line => line.Contains("Needs you: 11111111 Ship?"));
        Assert.Single(view.BoardTable.Rows.Cast<System.Data.DataRow>());
    }

    [Theory]
    [InlineData(true, false, "DECISIONS unavailable: question failed")]
    [InlineData(false, true, "ACTIVITY unavailable: activity failed")]
    public async Task BackgroundFailure_StaysInItsPaneAndPreservesBoard(bool failQuestions, bool failActivity, string error)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "Board survives", AgentRole.Developer);
        var questions = new GatedQuestions();
        var activity = new GatedActivity();
        using var app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness);
        var startup = new OwnerConsoleStartupLoader(Builder(harness, questions), activity, action => action());
        var fill = await startup.StartAsync(view.Render, view.Render, new(harness.Clock.GetUtcNow(), null, [], 0),
            _ => { }, TestContext.Current.CancellationToken);
        if (failQuestions) questions.Release.SetException(new InvalidOperationException("question failed"));
        else questions.Release.SetResult([]);
        if (failActivity) activity.Release.SetException(new InvalidOperationException("activity failed"));
        else activity.Release.SetResult(new([], null));
        await fill.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Board survives", Assert.Single(view.BoardTable.Rows.Cast<System.Data.DataRow>())["TITLE"]);
        Assert.Equal(error, Assert.Single(failQuestions ? view.DecisionLines : view.ActivityLines));
        Assert.DoesNotContain("loading...", view.DecisionLines);
        Assert.DoesNotContain("loading...", view.ActivityLines);
    }

    [Fact]
    public async Task DecisionsArrivingAfterActivityImmediatelyRaiseTheOwnerItem()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "Board first", Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        var questions = new GatedQuestions();
        var activity = new GatedActivity();
        var activityRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness);
        var startup = new OwnerConsoleStartupLoader(Builder(harness, questions), activity, action => action());
        var fill = await startup.StartAsync(view.Render, model =>
        {
            view.Render(model);
            if (model.ActivityState is null) activityRendered.TrySetResult();
        }, new(harness.Clock.GetUtcNow(), null, [], 0), _ => { }, TestContext.Current.CancellationToken);
        try
        {
            await activity.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            activity.Release.SetResult(new([], null));
            await activityRendered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Empty(view.ActivityLines);
            questions.Release.SetResult([new("q1", "11111111-first", OwnerQuestionKind.HumanInput, "Ship?")]);
            await fill.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Needs you: 11111111 Ship?", Assert.Single(view.ActivityLines));
        }
        finally
        {
            questions.Release.TrySetResult([]);
            activity.Release.TrySetResult(new([], null));
            await fill.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task HungSources_TimeoutIntoPaneErrorsAndReleaseStartup()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "Board survives timeout", AgentRole.Developer);
        var questions = new GatedQuestions();
        var activity = new GatedActivity();
        var clock = new PaneClock();
        using var app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness);
        var startup = new OwnerConsoleStartupLoader(Builder(harness, questions), activity,
            action => action(), clock);
        var fill = await startup.StartAsync(view.Render, view.Render, new(harness.Clock.GetUtcNow(), null, [], 0),
            _ => throw new InvalidOperationException("A timed-out activity load must not publish"), TestContext.Current.CancellationToken);
        try
        {
            await questions.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await activity.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await clock.BothTimersCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["loading..."], view.DecisionLines);
            Assert.Equal(["loading..."], view.ActivityLines);

            // Fire virtual deadlines explicitly; wall-clock elapsed time is never an assertion.
            clock.Advance(OwnerConsoleLoopOptions.Default.OperationBound);
            await fill.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Board survives timeout", Assert.Single(view.BoardTable.Rows.Cast<System.Data.DataRow>())["TITLE"]);
            Assert.Equal("DECISIONS unavailable: questions did not finish within 30s; the console is still running", Assert.Single(view.DecisionLines));
            Assert.Equal("ACTIVITY unavailable: activity did not finish within 30s; the console is still running", Assert.Single(view.ActivityLines));
            Assert.True(questions.Token.IsCancellationRequested);
            Assert.True(activity.Token.IsCancellationRequested);
        }
        finally
        {
            questions.Release.TrySetResult([]);
            activity.Release.TrySetResult(new([], null));
            await fill.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class PaneClock : TimeProvider
    {
        private readonly ManualStewardTimeProvider _timers = new();
        private int _created;
        internal readonly TaskCompletionSource BothTimersCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _timers.CreateTimer(callback, state, dueTime, period);
            if (Interlocked.Increment(ref _created) == 2) BothTimersCreated.TrySetResult();
            return timer;
        }
        internal void Advance(TimeSpan by) => _timers.Advance(by);
    }

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness, IOwnerQuestionSource questions) =>
        new(harness.State, questions, harness.Liveness, new Epics(), harness.Clock);
    private static OwnerConsoleFullScreenView View(IApplication app, OwnerConsoleHarness harness) => new(app,
        new(harness.Questions, harness.Answers, new Dialogs(), harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock), () => Task.CompletedTask);
    private sealed class GatedQuestions : IOwnerQuestionSource
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<IReadOnlyList<OwnerQuestion>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token)
        { Token = token; Entered.TrySetResult(); return await Release.Task.WaitAsync(token); }
    }
    private sealed class GatedActivity : IOwnerConsoleActivityLoader
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<OwnerConsoleActivityLoad> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyCollection<string> GoalIds = [];
        internal CancellationToken Token;
        public async Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        { Token = token; GoalIds = ids; Entered.TrySetResult(); return await Release.Task.WaitAsync(token); }
    }
    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException();
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException();
    }
}
