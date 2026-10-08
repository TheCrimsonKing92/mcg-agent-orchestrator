using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;

// Parallel-safe: lifecycle files belong to a unique temporary directory; GUI instances are headless.
public sealed class OwnerConsoleGoalStageActivityTests
{
    [Fact]
    public async Task LifecycleAndConductEvents_ShowFourTransitionsAndHideNoise()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-stage"), "# Console stages",
            [new(TaskId.New(), "Build", AgentRole.Developer), new(TaskId.New(), "Test", AgentRole.Tester)]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var directory = Path.Combine(Path.GetTempPath(), "owner-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = DateTimeOffset.UnixEpoch;
            var file = Path.Combine(directory, goal.Id.Value + ".jsonl");
            File.WriteAllLines(file,
            [
                Lifecycle(time, "TaskDispatched", goal.Tasks[0].Id.Value),
                JsonSerializer.Serialize(new { timestamp = time.AddSeconds(1), eventType = "TaskFailed",
                    taskId = goal.Tasks[1].Id.Value,
                    message = "Tester WORKER_RESULT rejected: merged structured finding state still has open blocking stable_id(s): finding1; automatic ownership routing required." })
            ]);
            var tail = new OwnerGoalLifecycleTail(directory);
            var events = tail.ReadNew([goal.Id.Value]).ToList();
            Assert.Equal(2, events.Count);
            events.AddRange([
                new(time.AddSeconds(2), "acceptance", "11111111", "ACCEPTANCE goal=11111111 result=passed"),
                new(time.AddSeconds(3), "loop-relaunch", goal.Id.Value, "LOOP_RELAUNCH_SCHEDULED goal=" + goal.Id.Value),
                new(time.AddSeconds(4), "goal", goal.Id.Value, "GOAL result=held reason=waiting"),
                new(time.AddSeconds(5), "gate-progress", goal.Id.Value, "heartbeat"),
                new(time.AddSeconds(6), "EVIDENCE_START", goal.Id.Value, "evidence"),
                new(time.AddSeconds(7), "sweep-owned-root-observed", goal.Id.Value, "observed")
            ]);
            using var app = Terminal.Gui.App.Application.Create();
            using var view = View(app, harness);
            view.Render(await Builder(harness).BuildAsync(new(time, time, events, 0)));

            Assert.Equal(new[]
            {
                $"{time.AddSeconds(3).ToLocalTime():HH:mm:ss} Landed: Console stages (11111111)",
                $"{time.AddSeconds(2).ToLocalTime():HH:mm:ss} Console stages: passed its tests, landing next",
                $"{time.AddSeconds(1).ToLocalTime():HH:mm:ss} Tester sent Console stages back: a problem needs correction",
                $"{time.ToLocalTime():HH:mm:ss} Developer started on Console stages"
            }, view.ActivityLines);

            Assert.Empty(tail.ReadNew([goal.Id.Value]));
            File.AppendAllText(file, Lifecycle(time.AddSeconds(8), "TaskCompleted", goal.Tasks[0].Id.Value) + "\n");
            var increment = Assert.Single(tail.ReadNew([goal.Id.Value]));
            Assert.Equal(time.AddSeconds(8), increment.Timestamp);
            Assert.Empty(tail.ReadNew([goal.Id.Value]));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RelaunchEvents_RenderLandingRestartAndFailureInWords()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-stage", "Console stages", AgentRole.Developer);
        var time = DateTimeOffset.UnixEpoch;
        OwnerConductEvent[] events =
        [
            new(time, "loop-relaunch", "11111111-stage", "LOOP_RELAUNCH_SCHEDULED goal=11111111-stage"),
            new(time.AddSeconds(1), "loop-handoff", null, "ACTIVATION_ADOPTED reason=none"),
            new(time.AddSeconds(2), "loop-handoff", null, "LOOP_HANDOFF_FAILED phase=restart continuing=true reason=publish_failed")
        ];
        using var app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness);
        view.Render(await Builder(harness).BuildAsync(new(time, time, events, 0)));
        Assert.Equal(new[]
        {
            $"{time.AddSeconds(2).ToLocalTime():HH:mm:ss} Conductor could not switch to the new code; still running the previous version (the new version could not be built)",
            $"{time.ToLocalTime():HH:mm:ss} Landed: Console stages (11111111)"
        }, view.ActivityLines);
        Assert.DoesNotContain(view.ActivityLines, line => line.Contains("handoff completed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TaskCompleted", "Reviewer passed Console stages")]
    [InlineData("TaskFailed", "Reviewer sent Console stages back: the worker could not finish")]
    public void RoleFinishes_RenderOutcomeWithoutPayload(string type, string phrase)
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UnixEpoch, eventType = type,
            taskId = "task1", role = "Reviewer", message = "{raw JSON}" });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, "11111111", out var item));
        Assert.Equal(phrase, OwnerActivityNarrator.DetailPhrase(item!, "Console stages"));
    }

    [Fact]
    public void EvidenceRoutingRecords_DoNotAnnounceRoleCompletion()
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UnixEpoch,
            eventType = "FindingEvidenceRequestRecorded", role = "Developer",
            message = "finding-evidence disposition=evidence-only-round-accepted; finding_id=finding1" });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, "11111111", out var item));
        Assert.False(OwnerActivityNarrator.Maps(item!));
        Assert.Null(OwnerActivityNarrator.DetailPhrase(item!, "Console stages"));
    }

    [Fact]
    public async Task DepartingGoal_DrainsFinalRoleFinishBeforeCursorEviction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-departing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var harness = new OwnerConsoleHarness();
            var goal = harness.AddGoal("11111111", "Departing goal", AgentRole.Reviewer);
            var builder = Builder(harness);
            await builder.BuildBoardAsync();
            var task = goal.Tasks.Single();
            var file = Path.Combine(directory, "11111111.jsonl");
            var tail = new OwnerGoalLifecycleTail(directory);
            File.WriteAllText(file, Lifecycle(DateTimeOffset.UnixEpoch, "TaskDispatched", task.Id.Value) + "\n");
            Assert.Single(tail.ReadNew([goal.Id.Value]));
            harness.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "passed");
            var snapshot = harness.Kernel.ExportSnapshot();
            harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item =>
                item with { Status = GoalStatus.Completed }).ToArray() });
            var board = await builder.BuildBoardAsync();
            Assert.Empty(board.Board);
            File.AppendAllText(file, Lifecycle(DateTimeOffset.UnixEpoch.AddSeconds(1), "TaskCompleted", task.Id.Value) + "\n");
            var final = Assert.Single(tail.ReadNew([]));
            using var app = Terminal.Gui.App.Application.Create();
            using var view = View(app, harness);
            view.Render(builder.WithActivity(board, new(DateTimeOffset.UnixEpoch, null, [final], 0)));
            Assert.Contains("Reviewer passed Departing goal", Assert.Single(view.ActivityLines));
            Assert.Empty(tail.ReadNew([]));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("result=started", null)]
    [InlineData("result=failed", "Console stages: failed its tests (the failure reason has not been recorded); awaiting the conductor's next step")]
    [InlineData("result=passed", "Console stages: passed its tests, landing next")]
    public void GateTransitions_RenderPlainPhrases(string detail, string? phrase)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "11111111", detail);
        Assert.Equal(phrase, OwnerActivityNarrator.Narrate([item], _ => "Console stages").SingleOrDefault()?.Phrase);
    }

    [Fact]
    public void StartupMerge_KeepsNewestEventsAcrossHistorySources()
    {
        var time = DateTimeOffset.UnixEpoch;
        var recent = Enumerable.Range(1, OwnerConsoleStartupActivity.MaxRawEvents)
            .Select(index => new OwnerConductEvent(time.AddSeconds(index), "acceptance", "11111111", "result=passed")).ToList();
        var newest = recent[^1];
        OwnerConsoleStartupActivity.Append(recent, new(time, "goal-lifecycle", "11111111", "TaskDispatched role=Developer"), OwnerConsoleStartupActivity.MaxRawEvents);
        Assert.Equal(OwnerConsoleStartupActivity.MaxRawEvents, recent.Count);
        Assert.Contains(newest, recent);
        Assert.DoesNotContain(recent, item => item.Timestamp == time);
    }

    [Fact]
    public async Task ActivityLoader_MergesHistoryAndPreservesLiveConductOffset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var time = DateTimeOffset.UnixEpoch;
            var path = Path.Combine(directory, "conduct.jsonl");
            var history = new OwnerConductEvent(time, "acceptance", "11111111", "result=passed");
            string Conduct(OwnerConductEvent item) => JsonSerializer.Serialize(new
                { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail });
            File.WriteAllText(path, Conduct(history) + "\n");
            File.WriteAllText(Path.Combine(directory, "11111111.jsonl"), Lifecycle(time, "TaskDispatched", "task1") + "\n");
            await using var loader = new OwnerConsoleActivityLoader(path, directory, TimeProvider.System);
            var loaded = await loader.LoadAsync(["11111111"], TestContext.Current.CancellationToken);
            Assert.Equal(2, loaded.Recent.Count);
            Assert.Contains(history, loaded.Recent);
            Assert.Contains(loaded.Recent, item => item.EventKind == "goal-lifecycle");
            Assert.Equal(time, loaded.LastActivity);
            Assert.Empty(loader.ReadNew(["11111111"]));
            var live = history with { Timestamp = time.AddSeconds(1), Detail = "result=failed" };
            File.AppendAllText(path, Conduct(live) + "\n");
            Assert.NotNull(loader.Events);
            Assert.Equal(live, await loader.Events.ReadAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void IncrementalTail_RetainsTornLinesResetsAndDropsRemovedGoals()
    {
        var directory = Path.Combine(Path.GetTempPath(), "owner-torn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "11111111.jsonl");
            var line = Lifecycle(DateTimeOffset.UnixEpoch, "TaskCompleted", "task1");
            var tail = new OwnerGoalLifecycleTail(directory);
            Assert.Empty(tail.ReadNew(["11111111"]));
            File.WriteAllText(file, "malformed\n" + line[..20]);
            Assert.Empty(tail.ReadNew(["11111111"]));
            File.AppendAllText(file, line[20..] + "\n");
            Assert.Single(tail.ReadNew(["11111111"]));
            Assert.Empty(tail.ReadNew(["11111111"]));
            File.WriteAllText(file, Lifecycle(DateTimeOffset.UnixEpoch, "TaskFailed", "t") + "\n");
            Assert.Contains("TaskFailed", Assert.Single(tail.ReadNew(["11111111"])).Detail);
            Assert.Empty(tail.ReadNew([]));
            Assert.Single(tail.ReadNew(["11111111"]));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Lifecycle(DateTimeOffset time, string type, string task) => JsonSerializer.Serialize(new
        { timestamp = time, eventType = type, taskId = task, message = "{raw JSON and commands}" });
    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness) =>
        new(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
    private static OwnerConsoleFullScreenView View(IApplication app, OwnerConsoleHarness harness) => new(app,
        new(harness.Questions, harness.Answers, new Dialogs(), harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock), () => Task.CompletedTask);
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
