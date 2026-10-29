using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
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
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0)));

        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);

        var text = Assert.Single(dialogs.Texts);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("11111111  Readable detail", lines[0]);
        Assert.Single(lines, line => line.Contains("Readable detail", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("Title: ", StringComparison.Ordinal));
        Assert.Equal("Status: in progress", lines[1]);
        Assert.Contains("Status: in progress", text);
        Assert.Contains("Stage: Blocked", text);
        Assert.Contains("Role: Developer", text);
        foreach (var task in goal.Tasks) Assert.Single(lines, line => line == $"  {task.RequiredRole}: {task.Status}");
        Assert.Contains("Waiting on: waiting for your approval", text);
        Assert.Equal(100, tail.Count);
        Assert.Single(lines, line => line.EndsWith("Developer started", StringComparison.Ordinal));
        Assert.Contains($"  {time.ToLocalTime():HH:mm:ss} Developer started", text);
        Assert.DoesNotContain(lines.SkipWhile(line => line != "Recent events:"), line => line.Contains("Readable detail"));
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.DoesNotContain("\"eventKind\"", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("evidence", text);
    }

    [Fact]
    public async Task RecordedHoldQuestionIsShownInDetailAndRecentEventsOmitTheTitle()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111", "Recovery for export", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            CurrentHold = new GoalHoldSnapshot("hold", "author-owner-question",
                "author-owner-question item=Goal:11111111 reason=choose recovery question=Retry the export lane? recommendation=Retry with evidence",
                harness.Clock.GetUtcNow())
        }).ToArray() });
        var tail = new Tail([JsonSerializer.Serialize(new { timestamp = harness.Clock.GetUtcNow(), eventType = "TaskDispatched",
            taskId = goal.Tasks.Single().Id.Value })]);
        var dialogs = new Dialogs();
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        view.Render(await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0)));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        var text = Assert.Single(dialogs.Texts);
        Assert.Contains("Waiting on: Retry the export lane?", text);
        Assert.DoesNotContain("a blocker needs review", text);
        var recent = text.Split(Environment.NewLine).SkipWhile(line => line != "Recent events:").Skip(1).ToArray();
        Assert.Contains(recent, line => line.EndsWith("Developer started", StringComparison.Ordinal));
        Assert.DoesNotContain(recent, line => line.Contains("Recovery for export", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("Verification passed (0): verify")]
    [InlineData("Verification failed (1): verify")]
    [InlineData("Dispatch execution failed (1): worker")]
    public async Task VerificationRecord_UsesNeutralPhraseRatherThanClaimingTaskPassed(string message)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111", "Verification outcome", AgentRole.Tester);
        var time = DateTimeOffset.UnixEpoch;
        var line = JsonSerializer.Serialize(new { timestamp = time, eventType = "TaskVerified",
            taskId = goal.Tasks.Single().Id.Value, message });
        var output = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, new Tail([line]), goal.Id.Value,
            output, TestContext.Current.CancellationToken);
        Assert.Contains("Recent events: none", output.Text);
        Assert.DoesNotContain("finished: passed", output.Text);
        Assert.DoesNotContain(message, output.Text);
    }

    [Fact]
    public void Render_FreshEvidence_InsertsExactProgressLinesImmediatelyAfterGate()
    {
        var lines = RenderEvidence(FreshEvidence()).Split(Environment.NewLine);
        var gateIndex = Array.FindIndex(lines, line => line.StartsWith("Gate: ", StringComparison.Ordinal));

        Assert.True(gateIndex >= 0, "The fixture must render a live Gate line.");
        Assert.Equal(new[]
        {
            "Evidence: attempt 2, elapsed 3m 12s",
            "Running: Infrastructure.Tests: AlphaTests +11 classes",
            "Tests so far: 5 passed, 1 failed",
            "Updated 4 s ago",
            "Last attempt: 6.5 min, failed, 163 tests",
            "Tasks:"
        }, lines.Skip(gateIndex + 1).Take(6));
    }

    [Theory]
    [InlineData(ConductorWatchProgressReporter.DefaultStallWarningMinutes * 60, false)]
    [InlineData(ConductorWatchProgressReporter.DefaultStallWarningMinutes * 60 + 1, true)]
    [InlineData(11 * 60, true)]
    public void Render_HeartbeatAge_UsesStrictExistingStallThreshold(int seconds, bool stalled)
    {
        var progress = FreshEvidence();
        var text = RenderEvidence(progress with { LastProgressAt = progress.ObservedAt.AddSeconds(-seconds) });

        if (stalled)
        {
            Assert.Contains($"no progress for {seconds / 60} min - possible stall", text);
            Assert.DoesNotContain("Updated ", text);
        }
        else
        {
            Assert.Contains($"Updated {seconds} s ago", text);
            Assert.DoesNotContain("possible stall", text);
        }
    }

    [Fact]
    public void Render_MissingTrxCounts_OmitsTestsLineAndKeepsOtherProgress()
    {
        var text = RenderEvidence(FreshEvidence() with { Passed = null, Failed = null });

        Assert.DoesNotContain("Tests so far:", text);
        Assert.Contains("Evidence: attempt 2, elapsed 3m 12s", text);
        Assert.Contains("Running: Infrastructure.Tests: AlphaTests +11 classes", text);
        Assert.Contains("Updated 4 s ago", text);
        Assert.Contains("Last attempt: 6.5 min, failed, 163 tests", text);
    }

    [Fact]
    public void Render_NoPreviousAttempt_OmitsLastAttemptLine()
    {
        var text = RenderEvidence(FreshEvidence() with { Previous = null });

        Assert.DoesNotContain("Last attempt:", text);
        Assert.Contains("Tests so far: 5 passed, 1 failed", text);
    }

    [Fact]
    public void Render_NoProgressRecord_KeepsGateAdjacentToTasks()
    {
        var lines = RenderEvidence(null).Split(Environment.NewLine);
        var gateIndex = Array.FindIndex(lines, line => line.StartsWith("Gate: ", StringComparison.Ordinal));

        Assert.True(gateIndex >= 0);
        Assert.Equal("Tasks:", lines[gateIndex + 1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("Evidence:", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_EmptyProgressRecord_OmitsAllEvidenceLines()
    {
        var now = FreshEvidence().ObservedAt;

        Assert.Equal(RenderEvidence(null), RenderEvidence(new OwnerConsoleEvidenceAttemptProgress(now)));
    }

    [Fact]
    public void Render_FutureTimestamps_OmitsDerivedAges()
    {
        var progress = FreshEvidence();
        var text = RenderEvidence(progress with
        {
            StartedAt = progress.ObservedAt.AddSeconds(1), LastProgressAt = progress.ObservedAt.AddSeconds(1)
        });

        Assert.Contains("Evidence: attempt 2" + Environment.NewLine, text);
        Assert.DoesNotContain("elapsed", text);
        Assert.DoesNotContain("Updated", text);
        Assert.DoesNotContain("possible stall", text);
    }

    private static OwnerConsoleEvidenceAttemptProgress FreshEvidence()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        return new(now, 2, now.AddSeconds(-192), "verification-check",
            "Infrastructure.Tests: AlphaTests +11 classes", now.AddSeconds(-4), 5, 1,
            new("11111111", "previous", 390, "failed", 163));
    }

    private static string RenderEvidence(OwnerConsoleEvidenceAttemptProgress? progress)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111", "Evidence progress", AgentRole.Developer);
        var gate = new OwnerConsoleGateProgress(true, "acceptance", [new(goal.Id.Value, "Evidence progress")],
            null, TimeSpan.Zero, [], 0, 0, 0, 0);
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal, null, [], Gate: gate, Evidence: progress),
            output, TimeZoneInfo.Utc);
        return output.Text;
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
