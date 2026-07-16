using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

public sealed class RunEventStoreTests
{
    [Xunit.Fact(DisplayName = "DogfoodLogStore_upserts_goal_entries_and_lists_recent")]
    public async Task DogfoodLogStoreUpsertsGoalEntriesAndListsRecent()
    {
        var store = new DogfoodLogStore(Path.Combine(CreateTempDirectory(), "dogfood-log.db"));

        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-1",
            "## 2026-07-02 - First",
            "summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - First\n\nsummary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-1",
            "## 2026-07-02 - First updated",
            "updated summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - First updated\n\nupdated summary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-2",
            "## 2026-07-02 - Second",
            "second summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough",
            "## 2026-07-02 - Second\n\nsecond summary"));

        var recent = await store.ListRecentAsync();
        var first = await store.GetByGoalIdAsync("goal-1");

        Assert.Equal(2, recent.Count);
        Assert.NotNull(first);
        Assert.Equal("updated summary", first!.Summary);
        Assert.Contains(recent, record => record.GoalId == "goal-2");
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_appends_records_without_mutating_prior_events")]
    public async Task SqliteRunEventStoreAppendsRecordsWithoutMutatingPriorEvents()
    {
        var store = new SqliteRunEventStore(TempDb());

        var first = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            "goal-1",
            "conductor:dispatch",
            "Begin",
            "starting",
            null));
        var second = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            "goal-1",
            "conductor:dispatch",
            "Completed",
            "started",
            null));

        var all = await store.ReadSinceAsync();
        var afterFirst = await store.ReadSinceAsync(first.Sequence);

        Assert.True(all.Select(e => e.Sequence).SequenceEqual([first.Sequence, second.Sequence]));
        Assert.Equal("Begin", all[0].Status);
        Assert.Equal("Completed", all[1].Status);
        Assert.Single(afterFirst);
        Assert.Equal(second.Sequence, afterFirst[0].Sequence);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_appends_matching_run_event")]
    public async Task GoalOperationJournalAppendsMatchingRunEvent()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Record journal event");

        GoalOperationJournal.Begin(root, goal, "conductor:dispatch", "Starting subscription dispatch.");

        var store = new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"));
        var events = await store.ReadSinceAsync(goalId: goal.Id.Value);
        var evt = Assert.Single(events);
        Assert.Equal(RunEventTypes.GoalOperation, evt.EventType);
        Assert.Equal("conductor:dispatch", evt.Operation);
        Assert.Equal("Begin", evt.Status);
        Assert.True(evt.Detail?.Contains("Starting subscription dispatch", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "ConductorTickPusher_appends_tick_to_run_event_store")]
    public async Task ConductorTickPusherAppendsTickToRunEventStore()
    {
        var db = TempDb();

        ConductorTickPusher.TryRecord(
            db,
            new BatchTickSummary(3, Advanced: 2, Held: 1, Escalated: 0, Retried: 0, Done: 1, WatchSleeping: false)
            {
                ProgressLines = ["TICK tick=3 eligible=4", "TICK_END tick=3 advanced=2 held=1 escalated=0 done=1"]
            });

        var events = await new SqliteRunEventStore(db).ReadSinceAsync();
        var evt = Assert.Single(events);
        Assert.Equal(RunEventTypes.ConductorTick, evt.EventType);
        Assert.Equal("conduct:tick", evt.Operation);
        Assert.True(evt.Detail?.Contains("advanced=2", StringComparison.Ordinal) == true);
        Assert.True(evt.PayloadJson?.Contains("TICK_END tick=3", StringComparison.Ordinal) == true);
    }

    [Xunit.Fact(DisplayName = "ConductorTickPusher_persists_bounded_tick_payload_without_full_disposition_snapshot")]
    public async Task ConductorTickPusherPersistsBoundedTickPayloadWithoutFullDispositionSnapshot()
    {
        var db = TempDb();
        var longLine = "TICK " + new string('x', 1000);
        var progressLines = Enumerable.Range(0, ConductorTickPusher.MaxPersistedProgressLines + 20)
            .Select(index => $"{longLine} {index}")
            .ToArray();

        ConductorTickPusher.TryRecord(
            db,
            new BatchTickSummary(9, Advanced: 1, Held: 2, Escalated: 3, Retried: 4, Done: 5, WatchSleeping: false)
            {
                ProgressLines = progressLines,
                OperatorDispositions =
                [
                    new ConductorOperatorDispositionSnapshot(
                        "goal-1",
                        OperatorDispositionState.Wait,
                        OperatorDispositionConfidence.High,
                        new string('r', 100_000),
                        "next",
                        DateTimeOffset.Parse("2026-07-16T12:00:00Z"),
                        [new string('b', 10_000)],
                        [new ConductorOperatorEvidenceSnapshot("log", "path", new string('e', 50_000))],
                        [])
                ]
            });

        var evt = Assert.Single(await new SqliteRunEventStore(db).ReadSinceAsync());
        Assert.NotNull(evt.PayloadJson);
        Assert.True(evt.PayloadJson!.Length < 10_000);
        using var doc = JsonDocument.Parse(evt.PayloadJson);
        var root = doc.RootElement;
        Assert.Equal(9, root.GetProperty("Tick").GetInt32());
        Assert.Equal(1, root.GetProperty("operatorDispositionCount").GetInt32());
        Assert.False(root.TryGetProperty("operatorDispositions", out _));
        var storedLines = root.GetProperty("progressLines").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(ConductorTickPusher.MaxPersistedProgressLines, storedLines.Length);
        Assert.All(storedLines, line => Assert.True(line!.Length <= ConductorTickPusher.MaxPersistedProgressLineChars));
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_maintenance_prunes_old_ticks_and_preserves_goal_operations_for_cursor_reads")]
    public async Task SqliteRunEventStoreMaintenancePrunesOldTicksAndPreservesGoalOperationsForCursorReads()
    {
        var store = new SqliteRunEventStore(TempDb());
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var goalId = "goal-1";
        var firstGoalOperation = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goalId,
            "conductor:dispatch",
            "Begin",
            "before prune",
            null,
            OccurredAt: now.AddDays(-30)));
        var oldTick = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.ConductorTick,
            null,
            "conduct:tick",
            "Active",
            "old tick",
            "{}",
            OccurredAt: now.AddDays(-20)));
        var recentTick = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.ConductorTick,
            null,
            "conduct:tick",
            "Active",
            "recent tick",
            "{}",
            OccurredAt: now.AddDays(-1)));
        var secondGoalOperation = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goalId,
            "conductor:dispatch",
            "Completed",
            "after prune",
            null,
            OccurredAt: now.AddDays(-20)));

        var result = await store.MaintainAsync(new RunEventMaintenanceOptions(
            TimeSpan.FromDays(7),
            MinConductorTickRowsToKeep: 1,
            Vacuum: false,
            UtcNow: now));

        Assert.False(result.Deferred);
        Assert.Equal(1, result.ConductorTickRowsDeleted);
        var all = await store.ReadSinceAsync(maxCount: 20);
        Assert.DoesNotContain(all, evt => evt.Sequence == oldTick.Sequence);
        Assert.Contains(all, evt => evt.Sequence == recentTick.Sequence);
        Assert.Contains(all, evt => evt.Sequence == firstGoalOperation.Sequence);
        Assert.Contains(all, evt => evt.Sequence == secondGoalOperation.Sequence);

        var afterFirst = await store.ReadSinceAsync(firstGoalOperation.Sequence, goalId: goalId);
        var resumed = Assert.Single(afterFirst);
        Assert.Equal(secondGoalOperation.Sequence, resumed.Sequence);
        Assert.Equal(RunEventTypes.GoalOperation, resumed.EventType);
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_maintenance_defers_when_database_write_lock_is_active")]
    public async Task SqliteRunEventStoreMaintenanceDefersWhenDatabaseWriteLockIsActive()
    {
        var db = TempDb();
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.ConductorTick,
            null,
            "conduct:tick",
            "Active",
            "old tick",
            "{}",
            OccurredAt: DateTimeOffset.Parse("2026-07-01T12:00:00Z")));
        await using var blocker = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;");
        await blocker.OpenAsync();
        await using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync();

        var result = await store.MaintainAsync(new RunEventMaintenanceOptions(
            TimeSpan.FromDays(1),
            MinConductorTickRowsToKeep: 0,
            Vacuum: true,
            UtcNow: DateTimeOffset.Parse("2026-07-16T12:00:00Z")));

        Assert.True(result.Deferred);
        Assert.Equal("database-busy", result.DeferredReason);
        Assert.Equal(0, result.ConductorTickRowsDeleted);

        await using var rollback = blocker.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
    }

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "run-events.db");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-run-events-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
