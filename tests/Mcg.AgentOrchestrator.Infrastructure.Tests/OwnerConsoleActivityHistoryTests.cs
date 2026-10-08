using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

// Parallel-safe: unique temporary directories, per-test kernels and headless views.
public sealed class OwnerConsoleActivityHistoryTests
{
    [Fact]
    public async Task StartupReadsARotatedLandingInsideTheLifecycleHorizonAndCountsTodaysLandings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
            scene.AddGoals();
            var now = scene.Harness.Clock.GetUtcNow();
            var path = Path.Combine(directory, "conduct-events.log");
            File.WriteAllLines(path, [Conduct(new(now, "loop-start", null, "LOOP_START"))]);
            File.WriteAllLines(Path.Combine(directory, "conduct-events-20260101000300.log"), [
                Conduct(new(now.AddMinutes(-2), "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED tick=1")),
                Conduct(new(now.AddMinutes(-2), "loop-relaunch", "22222222", "LOOP_RELAUNCH_SCHEDULED tick=1"))]);
            File.WriteAllLines(Path.Combine(directory, "11111111.jsonl"), [
                Lifecycle(now.AddMinutes(-3), "TaskDispatched", "Developer")]);
            await using var activity = new OwnerConsoleActivityLoader(path, directory, scene.Harness.Clock);
            var startup = new OwnerConsoleStartupLoader(scene.Builder, activity, action => action(), scene.Harness.Clock);
            OwnerConsoleActivityLoad? loaded = null;
            var board = OwnerConsoleStartupLoader.LoadingBoard(await scene.Builder.BuildBoardAsync());
            await startup.FillAsync(board, scene.View.Render, new(now, null, [], 0), value => loaded = value,
                TestContext.Current.CancellationToken);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.LandedToday);
            var landing = Assert.Single(scene.View.ActivityLines, line => line.Contains("Landed together: "));
            Assert.Contains("Search (11111111)", landing);
            Assert.Contains("Export (22222222)", landing);
            Assert.Contains("landed today: 2", scene.View.StatusText);
            Assert.DoesNotContain("hidden:", scene.View.StatusText);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GoalDetailFiltersNoiseCollapsesRepeatedDispatchAndPreservesTheFullTitleAndBlocker()
    {
        var time = DateTimeOffset.UnixEpoch;
        var tail = new Tail([
            Lifecycle(time, "GoalLifecycleDecision", "Developer"),
            Lifecycle(time.AddSeconds(1), "TaskNote", "Developer"),
            Lifecycle(time.AddSeconds(2), "TaskDispatched", "Developer"),
            Lifecycle(time.AddSeconds(3), "TaskDispatched", "Developer")]);
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(tail);
        var title = "Improve the search results " + new string('x', 150);
        var goal = scene.Harness.AddGoal("11111111", "# " + title + "\nDetails", AgentRole.Developer);
        var snapshot = scene.Harness.Kernel.ExportSnapshot();
        scene.Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(value => value with
            { CurrentHold = new GoalHoldSnapshot("hold", "owner-review-hold", "waiting_for_approval", time) }).ToArray() });
        await scene.Render([]);
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Enter);
        var detail = Assert.Single(scene.Dialogs.Messages).Text;
        Assert.Contains("Title: " + title, detail);
        Assert.Contains("Waiting on: waiting for your approval", detail);
        Assert.DoesNotContain("goal lifecycle decision", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("task note", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(detail.Split(Environment.NewLine), line => line.Contains("Developer started on " + title));
        Assert.Equal(100, tail.Requested);
    }

    [Fact]
    public void HistorySkipsMalformedSiblingsAndUsesNumericRotationSuffixOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "conduct-events.log");
            var now = new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
            File.WriteAllText(path, "{torn\n");
            File.WriteAllText(Path.Combine(directory, "conduct-events-20260101000300-10.log"), Conduct(new(now, "loop-relaunch", "11111111", "LOOP_RELAUNCH_NOT_REQUIRED")) + "\n");
            File.WriteAllText(Path.Combine(directory, "conduct-events-20260101000300-2.log"), "malformed\n" + Conduct(new(now.AddMinutes(-1), "acceptance", "22222222", "result=passed")) + "\n");
            var history = OwnerConsoleStartupActivity.ReadHistory(path, now.AddMinutes(-2), new Clock(now), TestContext.Current.CancellationToken);
            Assert.Equal(2, history.Recent.Count);
            Assert.Equal(1, history.LandedToday);
            Assert.Equal(now, history.LastActivity);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Conduct(OwnerConductEvent item) => JsonSerializer.Serialize(new
        { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail });
    private static string Lifecycle(DateTimeOffset time, string type, string role) => JsonSerializer.Serialize(new
        { timestamp = time, eventType = type, role, message = "private details" });
    private sealed class Tail(string[] lines) : IGoalEventTail
    {
        internal int Requested;
        public IReadOnlyList<string> ReadLast(string id, int count) { Requested = count; return lines.TakeLast(count).ToArray(); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
