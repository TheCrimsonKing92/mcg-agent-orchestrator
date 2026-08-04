using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

public sealed class RunEventStoreTests
{
    [Xunit.Fact(DisplayName = "GoalOperationJournal_reuses_run_event_store_per_path")]
    public void GoalOperationJournalReusesRunEventStorePerPath()
    {
        var db = TempDb();

        var first = GoalOperationJournal.GetRunEventStore(db);
        var second = GoalOperationJournal.GetRunEventStore(Path.GetFullPath(db));

        Assert.Same(first, second);
    }

    [Xunit.Fact(DisplayName = "GoalOperationJournal_retries_store_initialization_after_transient_schema_failure")]
    public void GoalOperationJournalRetriesStoreInitializationAfterTransientSchemaFailure()
    {
        var root = CreateTempDirectory();
        var blockedDirectory = Path.Combine(root, "temporarily-blocked");
        var db = Path.Combine(blockedDirectory, "run-events.db");
        File.WriteAllText(blockedDirectory, "blocks directory creation");

        Assert.ThrowsAny<IOException>(() => GoalOperationJournal.GetRunEventStore(db));

        File.Delete(blockedDirectory);
        Directory.CreateDirectory(blockedDirectory);
        var recovered = GoalOperationJournal.GetRunEventStore(db);

        Assert.NotNull(recovered);
        Assert.True(File.Exists(db));
        Assert.Same(recovered, GoalOperationJournal.GetRunEventStore(db));
    }

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

    [Xunit.Fact]
    public async Task Maintenance_PrunesTicksAndKeepsDurableEvents()
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
        var lifecycleStop = await store.AppendAsync(new RunEventAppend(
            RunEventTypes.ConductorLifecycle,
            null,
            "stop",
            "all-terminal",
            "reason=all-terminal ticks=12",
            "{\"generationId\":\"generation-1\"}",
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
        Assert.Contains(all, evt => evt.Sequence == lifecycleStop.Sequence);

        var lifecycleStops = await store.ReadByTypeSinceAsync(
            RunEventTypes.ConductorLifecycle,
            operation: "stop");
        Assert.Equal(lifecycleStop.Sequence, Assert.Single(lifecycleStops).Sequence);

        var afterFirst = await store.ReadSinceAsync(firstGoalOperation.Sequence, goalId: goalId);
        var resumed = Assert.Single(afterFirst);
        Assert.Equal(secondGoalOperation.Sequence, resumed.Sequence);
        Assert.Equal(RunEventTypes.GoalOperation, resumed.EventType);
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_maintenance_default_floor_prunes_aged_ticks_below_old_floor")]
    public async Task SqliteRunEventStoreMaintenanceDefaultFloorPrunesAgedTicksBelowOldFloor()
    {
        var store = new SqliteRunEventStore(TempDb());
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        for (var i = 0; i < 760; i++)
        {
            await AppendTickAsync(store, now.AddDays(-20).AddMinutes(i), "{}");
        }

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with { UtcNow = now });

        Assert.False(result.Deferred);
        Assert.Equal(10, result.AgedConductorTickRowsDeleted);
        Assert.Equal(0, result.OversizedConductorTickRowsDeleted);
        Assert.Equal(10, result.ConductorTickRowsDeleted);
        var remainingTicks = (await store.ReadSinceAsync(maxCount: 1000))
            .Count(evt => evt.EventType == RunEventTypes.ConductorTick);
        Assert.Equal(750, remainingTicks);
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_maintenance_payload_guard_prunes_oversized_tick_under_floor")]
    public async Task SqliteRunEventStoreMaintenancePayloadGuardPrunesOversizedTickUnderFloor()
    {
        var store = new SqliteRunEventStore(TempDb());
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var oversized = await AppendTickAsync(store, now.AddMinutes(-5), new string('x', 60_000));
        var healthy = await AppendTickAsync(store, now.AddMinutes(-4), "{}");

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with { UtcNow = now });

        Assert.False(result.Deferred);
        Assert.Equal(0, result.AgedConductorTickRowsDeleted);
        Assert.Equal(1, result.OversizedConductorTickRowsDeleted);
        Assert.Equal(1, result.ConductorTickRowsDeleted);
        Assert.True(result.DeletedPayloadBytesEstimate >= 60_000);
        var remaining = await store.ReadSinceAsync(maxCount: 10);
        Assert.DoesNotContain(remaining, evt => evt.Sequence == oversized.Sequence);
        Assert.Contains(remaining, evt => evt.Sequence == healthy.Sequence);
    }

    [Xunit.Fact(DisplayName = "SqliteRunEventStore_maintenance_honors_delete_batch_size_for_legacy_oversized_purge")]
    public async Task SqliteRunEventStoreMaintenanceHonorsDeleteBatchSizeForLegacyOversizedPurge()
    {
        var store = new SqliteRunEventStore(TempDb());
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        for (var i = 0; i < 1001; i++)
        {
            await AppendTickAsync(store, now.AddDays(-20).AddMinutes(i), new string('x', 20));
        }
        var oldHealthy = await AppendTickAsync(store, now.AddDays(-20).AddMinutes(1002), "{}");

        var result = await store.MaintainAsync(new RunEventMaintenanceOptions(
            TimeSpan.FromDays(7),
            MinConductorTickRowsToKeep: 750,
            Vacuum: false,
            UtcNow: now,
            MaxConductorTickPayloadBytes: 10,
            DeleteBatchSize: 250,
            LegacyOversizedConductorTickPurge: true));

        Assert.False(result.Deferred);
        Assert.Equal(0, result.AgedConductorTickRowsDeleted);
        Assert.Equal(1001, result.OversizedConductorTickRowsDeleted);
        Assert.True(result.MaxRowsDeletedInTransaction <= 250);
        Assert.Equal(1001 * 20, result.DeletedPayloadBytesEstimate);
        var remaining = await store.ReadSinceAsync(maxCount: 10);
        Assert.Contains(remaining, evt => evt.Sequence == oldHealthy.Sequence);
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_skips_when_last_marker_is_fresh")]
    public async Task RunEventMaintenanceCadenceSkipsWhenLastMarkerIsFresh()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.RunEventMaintenance,
            null,
            RunEventMaintenanceCadence.Operation,
            "Completed",
            "fresh marker",
            "{}",
            OccurredAt: now.AddHours(-1)));
        var oversized = await AppendTickAsync(store, now.AddMinutes(-10), new string('x', 60_000));

        var result = RunEventMaintenanceCadence.TryRunIfDue(db, logPath, () => now);

        Assert.True(result.Skipped);
        Assert.False(result.Attempted);
        var remaining = await store.ReadSinceAsync(maxCount: 10);
        Assert.Contains(remaining, evt => evt.Sequence == oversized.Sequence);
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_memory_skip_does_not_touch_conduct_log")]
    public async Task RunEventMaintenanceCadenceMemorySkipDoesNotTouchConductLog()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.RunEventMaintenance,
            null,
            RunEventMaintenanceCadence.Operation,
            "Completed",
            "fresh marker",
            "{}",
            OccurredAt: now.AddHours(-1)));

        Assert.True(RunEventMaintenanceCadence.TryRunIfDue(db, logPath, () => now).Skipped);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "sentinel");
        using var exclusive = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var second = RunEventMaintenanceCadence.TryRunIfDue(db, logPath, () => now.AddMinutes(1));

        Assert.True(second.Skipped);
        Assert.Equal("sentinel".Length, exclusive.Length);
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_self_defers_when_database_writer_is_busy")]
    public async Task RunEventMaintenanceCadenceSelfDefersWhenDatabaseWriterIsBusy()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var store = new SqliteRunEventStore(db);
        await AppendTickAsync(store, now.AddDays(-20), "{}");
        await using var blocker = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False;");
        await blocker.OpenAsync();
        await using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync();

        var result = RunEventMaintenanceCadence.TryRunIfDue(db, logPath, () => now);

        Assert.True(result.Attempted);
        Assert.True(result.Deferred);
        Assert.False(result.Failed);
        Assert.Equal("database-busy", result.Reason);
        Assert.Contains("run-events-maintenance", File.ReadAllText(logPath), StringComparison.Ordinal);

        await using var rollback = blocker.CreateCommand();
        rollback.CommandText = "ROLLBACK";
        await rollback.ExecuteNonQueryAsync();
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

    private static Task<RunEventRecord> AppendTickAsync(
        SqliteRunEventStore store,
        DateTimeOffset occurredAt,
        string payloadJson) =>
        store.AppendAsync(new RunEventAppend(
            RunEventTypes.ConductorTick,
            null,
            "conduct:tick",
            "Active",
            "tick",
            payloadJson,
            OccurredAt: occurredAt));

    private static string TempDb() => Path.Combine(CreateTempDirectory(), "run-events.db");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-run-events-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
