using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test kernel, fake event tail and uninitialized GUI instance.
public sealed class OwnerConsoleGoalDetailFormatterTests
{
    [Fact]
    public async Task BoardEnter_ShowsTaskLinesAndLocalEventPhrasesWithoutJson()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-detail"), "# Readable detail\nLong objective",
            [new(TaskId.New(), "Build", AgentRole.Developer), new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            CurrentHold = new GoalHoldSnapshot("hold", "owner-review-hold", "waiting_for_approval {metadata} evidence=[raw]", harness.Clock.GetUtcNow())
        }).ToArray() });
        goal = harness.Kernel.Goals.Single();
        var instant = DateTimeOffset.UnixEpoch;
        var offset = TimeZoneInfo.Local.GetUtcOffset(instant) == TimeSpan.Zero ? TimeSpan.FromHours(9) : TimeSpan.Zero;
        var time = instant.ToOffset(offset);
        var tail = new Tail(Enumerable.Range(0, 12).Select(index => JsonSerializer.Serialize(new
        {
            timestamp = time.AddSeconds(index), eventType = "TaskDispatched", taskId = goal.Tasks[0].Id.Value,
            message = "{\"eventKind\":\"secret\"}", detail = new { secret = "unreadable" }
        })).ToArray());
        var dialogs = new Dialogs();
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
            harness.State, tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0)));

        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);

        var text = Assert.Single(dialogs.Texts);
        Assert.Contains("Goal: " + goal.Id.Value, text);
        Assert.Contains("Title: Readable detail", text);
        Assert.Contains("Status: " + goal.Status, text);
        Assert.Contains("Stage: Developer", text);
        Assert.Contains("Role: Developer", text);
        var lines = text.Split(Environment.NewLine);
        foreach (var task in goal.Tasks) Assert.Single(lines, line => line == $"  {task.RequiredRole}: {task.Status}");
        Assert.Contains("Hold: owner review hold: waiting for approval", text);
        Assert.Equal(10, tail.Count);
        Assert.Equal(10, lines.Count(line => line.EndsWith("Developer started", StringComparison.Ordinal)));
        Assert.Contains($"  {time.AddSeconds(2).ToLocalTime():HH:mm:ss} Developer started", text);
        Assert.Contains($"  {time.AddSeconds(11).ToLocalTime():HH:mm:ss} Developer started", text);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.DoesNotContain("\"eventKind\"", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("evidence", text);
    }

    [Fact]
    public async Task MalformedTail_ShowsNoEventsWithoutLeakingPayload()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111", "Empty events", AgentRole.Developer);
        var output = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, new Tail(["{bad JSON}", "legacy raw line"]),
            goal.Id.Value, output, TestContext.Current.CancellationToken);
        Assert.Contains("Recent events: none", output.Text);
        Assert.DoesNotContain("{", output.Text);
        Assert.DoesNotContain("legacy", output.Text);
    }

    private sealed class Tail(string[] lines) : IGoalEventTail
    {
        internal int Count;
        public IReadOnlyList<string> ReadLast(string id, int count) { Count = count; return lines.TakeLast(count).ToArray(); }
    }
    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException();
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException();
    }
}
