using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

public sealed class OwnerConsoleStartupActivityTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InitialHeadlessViewShowsOnlyClassifiedHistoryNewestFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-activity-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                Line(1, "acceptance", "result=passed reason=older landing"),
                Line(4, "watch-transition", "unclassified internal transition"),
                "malformed JSON",
                Line(3, "host-health", "HOST_HEALTH_DEGRADED reason=newest health"),
                Line(2, "acceptance", "result=failed reason=middle failure")
            ]);
            var harness = new OwnerConsoleHarness();
            var dialogs = new Dialogs();
            var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
                harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
            using IApplication app = Terminal.Gui.App.Application.Create();
            using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);

            var (initial, _) = await OwnerConsoleFullScreenHost.BuildInitialViewModelAsync(builder, path,
                harness.Clock.GetUtcNow(), Epoch.AddSeconds(4), TestContext.Current.CancellationToken);
            view.Render(initial);

            Assert.Equal(2, view.ActivityLines.Count);
            Assert.Contains("failed its tests (the failure reason has not been recorded)", view.ActivityLines[0]);
            Assert.Contains("passed its tests, landing next", view.ActivityLines[1]);
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("newest health", StringComparison.Ordinal));
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("unclassified", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StartupHistoryUsesExistingCapAndKeepsNewestTimestamps()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-cap-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            // Deliberately reverse log order so the cap must select by timestamp.
            File.WriteAllLines(path, Enumerable.Range(0, OwnerConsoleViewModelBuilder.MaxActivityItems + 3)
                .Reverse().Select(index => Line(index, "acceptance", "result=passed " + index)));

            var history = OwnerConsoleStartupActivity.ReadRecent(path);

            Assert.Equal(OwnerConsoleViewModelBuilder.MaxActivityItems, history.Count);
            Assert.Equal(Epoch.AddSeconds(3), history[0].Timestamp);
            Assert.Equal(Epoch.AddSeconds(OwnerConsoleViewModelBuilder.MaxActivityItems + 2), history[^1].Timestamp);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingEmptyAndUnreadableHistoryDegradeToEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "owner-missing-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Assert.Empty(OwnerConsoleStartupActivity.ReadRecent(path));
            File.WriteAllText(path, string.Empty);
            Assert.Empty(OwnerConsoleStartupActivity.ReadRecent(path));
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Empty(OwnerConsoleStartupActivity.ReadRecent(path));
            await using var events = new OwnerConsoleStartupEventSource(path, TimeProvider.System);
            Assert.Null(events.LastActivity); // The host's source construction also tolerates unreadable history.
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LiveAppendDeduplicatesSeededEventsFiltersAndMaintainsCap()
    {
        var first = new OwnerConductEvent(Epoch, "acceptance", "11111111", "result=passed");
        var recent = new List<OwnerConductEvent> { first };
        OwnerConsoleStartupActivity.Append(recent, first with { });
        OwnerConsoleStartupActivity.Append(recent, first with { EventKind = "watch-transition" });
        Assert.Equal(first, Assert.Single(recent));
        foreach (var index in Enumerable.Range(1, OwnerConsoleViewModelBuilder.MaxActivityItems))
            OwnerConsoleStartupActivity.Append(recent, first with { Timestamp = Epoch.AddSeconds(index) });
        Assert.Equal(OwnerConsoleViewModelBuilder.MaxActivityItems, recent.Count);
        Assert.DoesNotContain(first, recent);
        Assert.Equal(Epoch.AddSeconds(OwnerConsoleViewModelBuilder.MaxActivityItems), recent[^1].Timestamp);
    }

    private static string Line(int seconds, string kind, string detail) => JsonSerializer.Serialize(new
    { timestamp = Epoch.AddSeconds(seconds), eventKind = kind, goalId = "11111111", detail });

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Startup must not prompt.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Startup must not prompt.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Startup must not open a dialog.");
    }
}
