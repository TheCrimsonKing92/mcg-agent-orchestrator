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
        Assert.Equal($"{DateTimeOffset.UnixEpoch.ToLocalTime():HH:mm:ss} 11111111 Board first gate passed", Assert.Single(view.ActivityLines));
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
        public async Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token)
        { Entered.TrySetResult(); return await Release.Task.WaitAsync(token); }
    }
    private sealed class GatedActivity : IOwnerConsoleActivityLoader
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<OwnerConsoleActivityLoad> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyCollection<string> GoalIds = [];
        public async Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> ids, CancellationToken token)
        { GoalIds = ids; Entered.TrySetResult(); return await Release.Task.WaitAsync(token); }
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
