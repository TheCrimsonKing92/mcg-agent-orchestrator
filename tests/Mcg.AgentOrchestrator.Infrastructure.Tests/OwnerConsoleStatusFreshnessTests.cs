using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

// Parallel-safe: the headless app and controllable clock belong to each test.
public sealed class OwnerConsoleStatusFreshnessTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task StatusShowsHiddenQuestionsAndBecomesStaleAfterThreeIntervals(int intervalSeconds)
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        var question = new OwnerQuestion("hidden1", "11111111", OwnerQuestionKind.HumanInput, "Proceed?");
        var questions = new Questions(new([], [new(question, "duplicate"), new(question with { ItemId = "hidden2" }, "duplicate")]));
        var builder = new OwnerConsoleViewModelBuilder(harness.State, questions, harness.Liveness, new Epics(), clock);
        var model = await builder.BuildAsync(new(clock.Now, null, [], 0));
        var options = OwnerConsoleLoopOptions.Default with { RefreshInterval = TimeSpan.FromSeconds(intervalSeconds) };
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock, options);
        view.Render(model);

        Assert.Contains("hidden: 2 | refreshed 00:05:00", view.StatusText);
        Assert.DoesNotContain("failed today:", view.StatusText);
        clock.Now += options.RefreshInterval * 3;
        view.RefreshStatus();
        Assert.Contains("refreshed 00:05:00", view.StatusText);
        Assert.DoesNotContain("stale", view.StatusText);
        clock.Now += TimeSpan.FromSeconds(1);
        view.RefreshStatus();
        Assert.Contains($"hidden: 2 | stale {Math.Max(1, intervalSeconds * 3 / 60)}m", view.StatusText);
        Assert.DoesNotContain("refreshed", view.StatusText);
    }

    [Fact]
    public void RefreshFailureKeepsTheOldTimestampAndSuccessfulRenderRestoresFreshness()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        var model = new OwnerConsoleViewModel(new(true, 0, 0, 0, null, 0, 1), [], [], []);
        view.Render(model);
        clock.Now += TimeSpan.FromMinutes(6);
        view.ShowNotice("state store unavailable", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        view.RefreshStatus();

        Assert.Contains("stale 6m | failed today: 1", view.StatusText);
        Assert.DoesNotContain("hidden:", view.StatusText);
        Assert.DoesNotContain("refreshed", view.StatusText);
        view.Render(model);
        Assert.Contains("refreshed 00:11:00 | failed today: 1", view.StatusText);
        Assert.DoesNotContain("stale", view.StatusText);
    }

    [Fact]
    public void RefreshTimeUsesTheInjectedLocalZone()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock
        { Zone = TimeZoneInfo.CreateCustomTimeZone("test-zone", TimeSpan.FromHours(2), "test-zone", "test-zone") };
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        view.Render(new(new(true, 0, 0, 0, null, 0), [], [], []));

        Assert.Contains("refreshed 02:05:00", view.StatusText);
    }

    private static OwnerConsoleFullScreenView View(IApplication app, OwnerConsoleHarness harness,
        TimeProvider clock, OwnerConsoleLoopOptions? options = null) =>
        new(app, new(harness.Questions, harness.Answers, new Dialogs(), harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, clock), () => Task.CompletedTask, clock: clock, options: options);

    private sealed class Questions(OwnerQuestionSnapshot snapshot) : IOwnerQuestionSource
    {
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token) => Task.FromResult(snapshot.Live);
        public Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(snapshot);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
