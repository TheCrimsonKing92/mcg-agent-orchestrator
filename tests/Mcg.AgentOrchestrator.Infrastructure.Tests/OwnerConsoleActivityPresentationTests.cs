using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test logs and headless application instances; no time-zone globals.
public sealed class OwnerConsoleActivityPresentationTests
{
    [Fact]
    public async Task HeadlessActivityUsesLocalTimeWordsAndTitlesAndOmitsDiagnostics()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-goal", "# Improve the owner console\nImplementation details", Mcg.AgentOrchestrator.Core.AgentRole.Developer);
        var instant = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // A non-local source offset also catches missing conversion on machines configured for UTC.
        var offset = TimeZoneInfo.Local.GetUtcOffset(instant) == TimeSpan.Zero ? TimeSpan.FromHours(9) : TimeSpan.Zero;
        var escalation = new OwnerConductEvent(instant.ToOffset(offset), "goal-escalation", "11111111-goal",
            "GOAL_ESCALATED result=escalated reason=owner_review_required task=internal-id");
        var passed = escalation with { Timestamp = escalation.Timestamp.AddSeconds(1), EventKind = "acceptance",
            Detail = "result=passed commit=internal-sha" };
        var diagnostic = escalation with { Timestamp = escalation.Timestamp.AddSeconds(2), EventKind = "state-log-divergence",
            Detail = "STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=0 by_design=7" };
        var path = Path.Combine(Path.GetTempPath(), "owner-human-activity-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            File.WriteAllLines(path, new[] { escalation, passed, diagnostic }.Select(LogLine));
            var builder = Builder(harness);
            var dialogs = new Dialogs();
            var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
                harness.State, new Tail(escalation.Detail), harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            using IApplication app = Terminal.Gui.App.Application.Create();
            using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
            var (model, recent) = await OwnerConsoleFullScreenHost.BuildInitialViewModelAsync(builder, path,
                harness.Clock.GetUtcNow(), null, TestContext.Current.CancellationToken);
            view.Render(model);

            Assert.Equal(new[]
            {
                $"{passed.Timestamp.ToLocalTime():HH:mm:ss} 11111111 Improve the owner console gate passed",
                $"{escalation.Timestamp.ToLocalTime():HH:mm:ss} 11111111 Improve the owner console escalated: owner review required"
            }, view.ActivityLines);
            Assert.Equal(2, recent.Count);
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("STATE_LOG_DIVERGENCE", StringComparison.Ordinal));
            Assert.DoesNotContain(view.ActivityLines, line => line.Contains("internal-", StringComparison.Ordinal));
            Assert.Equal(escalation.Detail, model.Activity[1].Detail); // Original diagnostics remain intact.

            // Live append and direct model inputs obey the same visibility rule as the startup scan.
            OwnerConsoleStartupActivity.Append(recent, diagnostic);
            Assert.Equal(2, recent.Count);
            view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [escalation, diagnostic, passed], 0)));
            Assert.Equal(2, view.ActivityLines.Count);
            await view.HandleKeyAsync(Key.Tab);
            await view.HandleKeyAsync(Key.Enter);
            Assert.Contains(escalation.Detail, Assert.Single(dialogs.Texts));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=0 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=0 stored_only=3 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=1 repeated=0 stored_only=0 by_design=9", true)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 repeated=2 stored_only=0 by_design=9", true)]
    [InlineData("STATE_LOG_DIVERGENCE lost=unknown repeated=0 by_design=9", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=0 lost=1 repeated=0 by_design=9", false)]
    public void DivergenceRequiresPositiveFaultEvidence(string detail, bool visible)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "state-log-divergence", "11111111", detail);
        Assert.Equal(visible, OwnerConsoleStartupActivity.IsOperatorEvent(item));
    }

    [Theory]
    [InlineData("author", "kind=ask-owner item=internal-id", "needs your input")]
    [InlineData("acceptance", "result=failed code=1", "gate failed")]
    [InlineData("acceptance", "result=blocked reason=missing_evidence", "gate blocked: missing evidence")]
    [InlineData("goal-escalation", "ownerless-hold-stalled state=blocked heldForSeconds=30 blocker=waiting for owner approval", "escalated: waiting for owner approval")]
    public async Task StructuredEventsRenderPlainPhrasesWithoutRawFields(string kind, string detail, string phrase)
    {
        var harness = new OwnerConsoleHarness();
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, kind, "unknown-goal", detail);
        var model = await Builder(harness).BuildAsync(new(harness.Clock.GetUtcNow(), null, [item], 0));
        var activity = Assert.Single(model.Activity);
        Assert.Equal(phrase, activity.Phrase);
        Assert.Equal(string.Empty, activity.GoalTitle);
        Assert.Equal(detail, activity.Detail);
        Assert.Equal($"{item.Timestamp.ToLocalTime():HH:mm:ss} unknown- {phrase}", OwnerConsoleActivityPresentation.Line(activity));
    }

    [Fact]
    public async Task ActivityScrollKeepsTheViewedLineWhenNewEventsArrive()
    {
        var harness = new OwnerConsoleHarness();
        var first = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "11111111", "result=passed");
        var second = first with { Timestamp = first.Timestamp.AddSeconds(1) };
        var builder = Builder(harness);
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [first, second], 0)));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);
        var viewed = view.ActivityLines[1];

        var third = first with { Timestamp = first.Timestamp.AddSeconds(2) };
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [first, second, third], 0)));

        Assert.Equal(2, view.ActivityPane.SelectedItem);
        Assert.Equal(viewed, view.ActivityLines[view.ActivityPane.SelectedItem!.Value]);
    }

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness) =>
        new(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);

    private static string LogLine(OwnerConductEvent item) => JsonSerializer.Serialize(new
    { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail });

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Tail(string detail) : IGoalEventTail
    {
        public IReadOnlyList<string> ReadLast(string goalId, int count) => [detail];
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Activity must not prompt.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Activity must not prompt.");
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }
}
