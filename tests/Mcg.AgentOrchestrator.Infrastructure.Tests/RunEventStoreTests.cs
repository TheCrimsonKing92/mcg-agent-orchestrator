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
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "## 2026-07-02 - First\n\nsummary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-1",
            "## 2026-07-02 - First updated",
            "updated summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "## 2026-07-02 - First updated\n\nupdated summary"));
        await store.UpsertAsync(new DogfoodLogAppend(
            "goal-2",
            "## 2026-07-02 - Second",
            "second summary",
            "tests passed",
            "Model fit: OpenAI/gpt-5.5 - adequate - storage test - enough", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
            "## 2026-07-02 - Second\n\nsecond summary"));

        var recent = await store.ListRecentAsync();
        var first = await store.GetByGoalIdAsync("goal-1");

        Assert.Equal(2, recent.Count);
        Assert.NotNull(first);
        Assert.Equal("updated summary", first!.Summary);
        Assert.Contains(recent, record => record.GoalId == "goal-2");
    }

    // Dogfood fixtures are parallel-safe: unique directories and pooling-disabled connections.
    [Xunit.Fact]
    public async Task DogfoodSetupLegacyDatabasePreservesEveryFieldAndIsIdempotent()
    {
        var path = Path.Combine(CreateTempDirectory(), "dogfood-log.db");
        var expected = CreateLegacyDogfoodDatabase(path);
        using (var legacy = OpenDogfoodConnection(path))
            Assert.Null(StoreSchemaVersions.Read(legacy, StoreSchemaRegistry.DogfoodLog.StoreName));

        DogfoodLogStore.Setup(path);

        var store = DogfoodLogStore.OpenReadOnly(path);
        Assert.Equal(expected.Reverse(), await store.ListRecentAsync());
        foreach (var record in expected)
            Assert.Equal(record, await store.GetByGoalIdAsync(record.GoalId));
        using (var connection = OpenDogfoodConnection(path, SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.DogfoodLog.StoreName));
            RunDogfoodSql(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
        }
        var bytes = File.ReadAllBytes(path);
        var schema = DogfoodSchemaSnapshot(path);

        DogfoodLogStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, DogfoodSchemaSnapshot(path));
        Assert.Equal(expected.Reverse(), await DogfoodLogStore.OpenReadOnly(path).ListRecentAsync());
    }

    [Xunit.Fact]
    public void DogfoodSetupNewerVersionLeavesBytesSchemaAndJournalModeUntouched()
    {
        var path = Path.Combine(CreateTempDirectory(), "dogfood-log.db");
        DogfoodLogStore.Setup(path);
        using (var connection = OpenDogfoodConnection(path, SqliteOpenMode.ReadWrite))
        {
            RunDogfoodSql(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'future'");
            RunDogfoodSql(connection, "PRAGMA journal_mode=DELETE");
        }
        var bytes = File.ReadAllBytes(path);
        var schema = DogfoodSchemaSnapshot(path);

        DogfoodLogStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, DogfoodSchemaSnapshot(path));
        using var readBack = OpenDogfoodConnection(path);
        Assert.Equal(2, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.DogfoodLog.StoreName));
        using var command = readBack.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("delete", command.ExecuteScalar());
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing", StoreSchemaState.Missing)]
    [Xunit.InlineData("unversioned", StoreSchemaState.Missing)]
    [Xunit.InlineData("missing-record", StoreSchemaState.Missing)]
    [Xunit.InlineData("older", StoreSchemaState.Older)]
    [Xunit.InlineData("newer", StoreSchemaState.Newer)]
    public void DogfoodOpenReadOnlyRequiresCurrentSchemaWithoutMutatingDatabase(string scenario, StoreSchemaState state)
    {
        var path = Path.Combine(CreateTempDirectory(), "absent-directory", "dogfood-log.db");
        if (scenario == "unversioned")
            CreateLegacyDogfoodDatabase(path);
        else if (scenario != "missing")
        {
            DogfoodLogStore.Setup(path);
            using var connection = OpenDogfoodConnection(path, SqliteOpenMode.ReadWrite);
            RunDogfoodSql(connection, scenario switch
            {
                "missing-record" => "DELETE FROM store_schema_versions",
                "older" => "UPDATE store_schema_versions SET version = 0",
                _ => "UPDATE store_schema_versions SET version = 2"
            });
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var schema = File.Exists(path) ? DogfoodSchemaSnapshot(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => DogfoodLogStore.OpenReadOnly(path));

        Assert.Equal($"Dogfood log store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(schema, DogfoodSchemaSnapshot(path));
        }
    }

    [Xunit.Fact]
    public async Task DogfoodOpenReadOnlyReturnsWriterRowsAndRejectsWritesWithoutMutation()
    {
        var path = Path.Combine(CreateTempDirectory(), "dogfood-log.db");
        var writer = new DogfoodLogStore(path);
        var expected = await writer.UpsertAsync(new DogfoodLogAppend(
            "current-goal", "header", "summary", "gate", "model", "markdown",
            DateTimeOffset.Parse("2026-07-02T12:00:00Z")));
        var bytes = File.ReadAllBytes(path);
        var schema = DogfoodSchemaSnapshot(path);

        var reader = DogfoodLogStore.OpenReadOnly(path);
        Assert.Equal(expected, Assert.Single(await reader.ListRecentAsync()));
        Assert.Equal(expected, await reader.GetByGoalIdAsync(expected.GoalId));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, DogfoodSchemaSnapshot(path));
        var error = await Assert.ThrowsAsync<SqliteException>(() => reader.UpsertAsync(
            new DogfoodLogAppend("forbidden-goal", "header", "summary", "gate", "model", "markdown")));

        Assert.Equal(8, error.SqliteErrorCode);
        Assert.Null(await reader.GetByGoalIdAsync("forbidden-goal"));
        Assert.Equal(expected, Assert.Single(await reader.ListRecentAsync()));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, DogfoodSchemaSnapshot(path));
    }

    [Xunit.Fact]
    public async Task DogfoodSetupAndReadOnlyReadsKeepWalJournalMode()
    {
        var path = Path.Combine(CreateTempDirectory(), "dogfood-log.db");
        for (var run = 0; run < 2; run++)
        {
            DogfoodLogStore.Setup(path);
            Assert.Empty(await DogfoodLogStore.OpenReadOnly(path).ListRecentAsync());
            using var connection = OpenDogfoodConnection(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            Assert.Equal("wal", command.ExecuteScalar());
        }
        // SQLite may create WAL/SHM sidecars on read; only main-file immutability is asserted.
    }

    [Xunit.Fact]
    public void DogfoodSetupVersionFailureRollsBackTableAndIndexCreation()
    {
        var path = Path.Combine(CreateTempDirectory(), "dogfood-log.db");
        using (var connection = OpenDogfoodConnection(path, SqliteOpenMode.ReadWriteCreate))
            RunDogfoodSql(connection, "CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER)");

        Assert.Throws<SqliteException>(() => DogfoodLogStore.Setup(path));

        using var readBack = OpenDogfoodConnection(path);
        using var command = readBack.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name IN ('dogfood_log', 'ix_dogfood_log_recorded_at')";
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Null(StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.DogfoodLog.StoreName));
    }

    internal static DogfoodLogRecord[] CreateLegacyDogfoodDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenDogfoodConnection(path, SqliteOpenMode.ReadWriteCreate);
        // Frozen pre-versioning schema; deliberately does not call the current setup code.
        RunDogfoodSql(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS dogfood_log (
                seq               INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id           TEXT NOT NULL UNIQUE,
                recorded_at       TEXT NOT NULL,
                header            TEXT NOT NULL,
                summary           TEXT NOT NULL,
                operator_gate     TEXT NOT NULL,
                model_fit         TEXT NOT NULL,
                rendered_markdown TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_dogfood_log_recorded_at ON dogfood_log(recorded_at DESC, seq DESC);
            """);
        var records = new[]
        {
            new DogfoodLogRecord(1, "legacy-1", DateTimeOffset.Parse("2026-07-01T12:00:00Z"),
                "first header", "first summary", "first gate", "first model", "first markdown"),
            new DogfoodLogRecord(2, "legacy-2", DateTimeOffset.Parse("2026-07-02T12:00:00+02:00"),
                "second header", "second summary", "second gate", "second model", "second markdown")
        };
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO dogfood_log (seq, goal_id, recorded_at, header, summary, operator_gate, model_fit, rendered_markdown)
                VALUES ($seq, $goal, $time, $header, $summary, $gate, $model, $markdown)
                """;
            command.Parameters.AddWithValue("$seq", record.Sequence);
            command.Parameters.AddWithValue("$goal", record.GoalId);
            command.Parameters.AddWithValue("$time", record.RecordedAt.ToString("O"));
            command.Parameters.AddWithValue("$header", record.Header);
            command.Parameters.AddWithValue("$summary", record.Summary);
            command.Parameters.AddWithValue("$gate", record.OperatorGate);
            command.Parameters.AddWithValue("$model", record.ModelFit);
            command.Parameters.AddWithValue("$markdown", record.RenderedMarkdown);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        return records;
    }

    internal static string[] DogfoodSchemaSnapshot(string path)
    {
        using var connection = OpenDogfoodConnection(path);
        var rows = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, coalesce(sql, '') FROM sqlite_schema ORDER BY name";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)));
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name = 'store_schema_versions'";
        if ((long)command.ExecuteScalar()! > 0)
        {
            command.CommandText = "SELECT store_name, version, applied_at FROM store_schema_versions ORDER BY store_name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add($"{reader.GetString(0)}|{reader.GetInt32(1)}|{reader.GetString(2)}");
        }
        return rows.ToArray();
    }

    private static SqliteConnection OpenDogfoodConnection(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void RunDogfoodSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
        Assert.Equal(SqliteMaintenanceDisposition.Incomplete, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.WorkCapReached, result.Reason);
        Assert.Equal(0, result.AgedConductorTickRowsDeleted);
        Assert.Equal(1000, result.OversizedConductorTickRowsDeleted);
        Assert.Equal(1, result.RemainingEligibleRows);
        Assert.True(result.MaxRowsDeletedInTransaction <= 250);
        Assert.Equal(1000 * 20, result.DeletedPayloadBytesEstimate);
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

    [Xunit.Fact]
    public async Task RunEventMaintenanceCadence_FailedArtifactSweepRetriesOnlyAfterPersistedBackoff()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var markerAt = now.AddMinutes(-1);
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.RunEventMaintenance,
            null,
            RunEventMaintenanceCadence.Operation,
            "Completed",
            "fresh marker",
            "{}",
            OccurredAt: markerAt));
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.EvidenceRetention,
            null,
            RunEventMaintenanceCadence.ArtifactRetentionOperation,
            "Failed",
            "failed retention sweep",
            "{}",
            OccurredAt: markerAt));
        var calls = 0;
        var completedMaintenance = new RunEventMaintenanceResult(
            Deferred: false,
            DeferredReason: null,
            ConductorTickRowsDeleted: 0,
            AgedConductorTickRowsDeleted: 0,
            OversizedConductorTickRowsDeleted: 0,
            DeletedPayloadBytesEstimate: 0,
            MaxRowsDeletedInTransaction: 0,
            Duration: TimeSpan.Zero,
            BytesBefore: 0,
            BytesAfter: 0,
            VacuumRequested: false,
            VacuumCompleted: false,
            VacuumDeferred: false);

        var beforeBackoff = RunEventMaintenanceCadence.TryRunIfDue(
            db,
            logPath,
            () => now,
            (_, _) =>
            {
                calls++;
                return completedMaintenance;
            });
        var afterBackoff = RunEventMaintenanceCadence.TryRunIfDue(
            db,
            logPath,
            () => markerAt.Add(RunEventMaintenanceCadence.ArtifactFailureRetryInterval).AddSeconds(1),
            (_, _) =>
            {
                calls++;
                return completedMaintenance;
            });

        Assert.True(beforeBackoff.Skipped);
        Assert.True(afterBackoff.Attempted);
        Assert.Equal(1, calls);
    }

    [Xunit.Fact]
    public async Task RunEventMaintenanceCadence_DeferredDatabaseReceiptDoesNotResolveFailedArtifactSweep()
    {
        var root = CreateTempDirectory();
        var executionDirectory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(executionDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory);
        InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var failedAt = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.EvidenceRetention,
            null,
            RunEventMaintenanceCadence.ArtifactRetentionOperation,
            "Failed",
            "failed retention sweep",
            "{}",
            OccurredAt: failedAt));
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.RunEventMaintenance,
            null,
            RunEventMaintenanceCadence.Operation,
            "Deferred",
            "database maintenance deferred",
            JsonSerializer.Serialize(new
            {
                disposition = SqliteMaintenanceDisposition.Deferred.ToString(),
                nextAttemptAt = failedAt.AddMinutes(45),
                consecutiveNoProgressAttempts = 1
            }),
            OccurredAt: failedAt.AddMinutes(30)));
        var completedMaintenance = new RunEventMaintenanceResult(
            Deferred: false,
            DeferredReason: null,
            ConductorTickRowsDeleted: 0,
            AgedConductorTickRowsDeleted: 0,
            OversizedConductorTickRowsDeleted: 0,
            DeletedPayloadBytesEstimate: 0,
            MaxRowsDeletedInTransaction: 0,
            Duration: TimeSpan.Zero,
            BytesBefore: 0,
            BytesAfter: 0,
            VacuumRequested: false,
            VacuumCompleted: false,
            VacuumDeferred: false);
        var successfulRetention = new StorageRetentionResult(0, 0, 0, 0, 0, 0, "retry-success", []);
        var artifactCalls = 0;

        var result = RunEventMaintenanceCadence.TryRunIfDue(
            db,
            logPath,
            () => failedAt.Add(RunEventMaintenanceCadence.ArtifactFailureRetryInterval).AddSeconds(1),
            maintenanceOperation: (_, _) => completedMaintenance,
            workspace: workspace,
            artifactRetentionOperation: (_, _, _, _) =>
            {
                artifactCalls++;
                return successfulRetention;
            });

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Same(successfulRetention, result.ArtifactRetention);
        Assert.Equal(1, artifactCalls);
    }

    [Xunit.Fact]
    public void RunEventMaintenanceCadence_FailedArtifactSweepSetsInMemoryBackoff()
    {
        var root = CreateTempDirectory();
        var executionDirectory = Path.Combine(root, "workspace");
        Directory.CreateDirectory(executionDirectory);
        var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory);
        InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var artifactCalls = 0;
        var completedMaintenance = new RunEventMaintenanceResult(
            Deferred: false,
            DeferredReason: null,
            ConductorTickRowsDeleted: 0,
            AgedConductorTickRowsDeleted: 0,
            OversizedConductorTickRowsDeleted: 0,
            DeletedPayloadBytesEstimate: 0,
            MaxRowsDeletedInTransaction: 0,
            Duration: TimeSpan.Zero,
            BytesBefore: 0,
            BytesAfter: 0,
            VacuumRequested: false,
            VacuumCompleted: false,
            VacuumDeferred: false);
        var failedRetention = new StorageRetentionResult(
            WorkerArtifactsDeleted: 0,
            WorkerLogsCompressed: 0,
            SuccessfulTrxReceiptsWritten: 0,
            AcceptanceArtifactsDeleted: 0,
            PromptArtifactsDeleted: 0,
            GoalJournalsArchived: 0,
            SweepId: "failed-sweep",
            Decisions:
            [
                new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.RunEvents,
                    EvidenceRetentionAction.Failed,
                    workspace.OrchestratorDirectory,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "sweep-failed")
            ]);

        StorageRetentionResult FailRetention(
            OrchestratorWorkspace _workspace,
            IReadOnlyCollection<StorageRetentionGoal> _goals,
            DateTimeOffset _now,
            string? _mtpResultsRoot)
        {
            artifactCalls++;
            return failedRetention;
        }

        RunEventMaintenanceCadenceResult RunAt(DateTimeOffset timestamp) =>
            RunEventMaintenanceCadence.TryRunIfDue(
                db,
                logPath,
                () => timestamp,
                maintenanceOperation: (_, _) => completedMaintenance,
                workspace: workspace,
                artifactRetentionOperation: FailRetention);

        var failed = RunAt(now);
        File.Delete(db);
        File.Delete(logPath);
        var beforeBackoff = RunAt(now.AddMinutes(1));
        var afterBackoff = RunAt(now.Add(RunEventMaintenanceCadence.ArtifactFailureRetryInterval).AddSeconds(1));

        Assert.True(failed.Failed);
        Assert.True(beforeBackoff.Skipped);
        Assert.True(afterBackoff.Attempted);
        Assert.Equal(2, artifactCalls);
    }

    [Xunit.Fact]
    public void RunEventMaintenanceCadence_ExceptionSetsInMemoryBackoff()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var calls = 0;

        RunEventMaintenanceCadenceResult RunAt(DateTimeOffset timestamp) =>
            RunEventMaintenanceCadence.TryRunIfDue(
                db,
                logPath,
                () => timestamp,
                maintenanceOperation: (_, _) =>
                {
                    calls++;
                    throw new IOException("injected maintenance failure");
                });

        var failed = RunAt(now);
        var beforeBackoff = RunAt(now.AddMinutes(1));
        var afterBackoff = RunAt(now.Add(RunEventMaintenanceCadence.ArtifactFailureRetryInterval).AddSeconds(1));

        Assert.True(failed.Failed);
        Assert.True(beforeBackoff.Skipped);
        Assert.True(afterBackoff.Failed);
        Assert.Equal(2, calls);
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_production_default_runs_store_maintenance")]
    public async Task RunEventMaintenanceCadenceProductionDefaultRunsStoreMaintenance()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var store = new SqliteRunEventStore(db);
        var oversized = await AppendTickAsync(store, now.AddMinutes(-10), new string('x', 60_000));

        var result = RunEventMaintenanceCadence.TryRunIfDue(db, logPath, () => now);

        Assert.True(result.Attempted);
        Assert.False(result.Deferred);
        Assert.False(result.Failed);
        var maintenance = Assert.IsType<RunEventMaintenanceResult>(result.Maintenance);
        Assert.Equal(1, maintenance.OversizedConductorTickRowsDeleted);
        var remaining = await store.ReadSinceAsync(maxCount: 10);
        Assert.DoesNotContain(remaining, evt => evt.Sequence == oversized.Sequence);
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
        var maintenanceCalls = 0;
        RunEventMaintenanceOptions? observedOptions = null;
        var deferredMaintenance = new RunEventMaintenanceResult(
            Deferred: true,
            DeferredReason: "database-busy",
            ConductorTickRowsDeleted: 0,
            AgedConductorTickRowsDeleted: 0,
            OversizedConductorTickRowsDeleted: 0,
            DeletedPayloadBytesEstimate: 0,
            MaxRowsDeletedInTransaction: 0,
            Duration: TimeSpan.Zero,
            BytesBefore: 0,
            BytesAfter: 0,
            VacuumRequested: false,
            VacuumCompleted: false,
            VacuumDeferred: false);

        var result = RunEventMaintenanceCadence.TryRunIfDue(
            db,
            logPath,
            () => now,
            (_, options) =>
            {
                maintenanceCalls++;
                observedOptions = options;
                return deferredMaintenance;
            });

        Assert.Equal(1, maintenanceCalls);
        Assert.NotNull(observedOptions);
        Assert.Equal(now, observedOptions.UtcNow);
        Assert.False(observedOptions.Vacuum);
        Assert.True(result.Attempted);
        Assert.True(result.Deferred);
        Assert.False(result.Failed);
        Assert.Equal("database-busy", result.Reason);
        Assert.Same(deferredMaintenance, result.Maintenance);
        Assert.Contains("run-events-maintenance", File.ReadAllText(logPath), StringComparison.Ordinal);
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

        try
        {
            var result = await store.MaintainAsync(new RunEventMaintenanceOptions(
                TimeSpan.FromDays(1),
                MinConductorTickRowsToKeep: 0,
                Vacuum: true,
                OfflineVacuumAuthorized: true,
                UtcNow: DateTimeOffset.Parse("2026-07-16T12:00:00Z"),
                MaintenanceLockCommandTimeoutSeconds: 1));

            Assert.True(result.Deferred);
            Assert.Equal("database-busy", result.DeferredReason);
            Assert.Equal(0, result.ConductorTickRowsDeleted);
        }
        finally
        {
            await using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync();
        }
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenance_terminal_goal_operation_outside_threshold_is_removed")]
    public async Task TerminalGoalOperationOutsideThresholdIsRemoved()
    {
        var db = TempDb();
        var store = new SqliteRunEventStore(db);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        await AppendGoalOperationAsync(store, "terminal-goal", now.AddDays(-31));
        await AppendGoalOperationAsync(store, "terminal-goal", now.AddDays(-29));

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = ["terminal-goal"]
        });

        Assert.Equal(1, result.TerminalGoalOperationRowsDeleted);
        var remaining = await store.ReadSinceAsync(goalId: "terminal-goal");
        Assert.Single(remaining);
        Assert.Equal(now.AddDays(-29), remaining[0].OccurredAt);
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenance_goal_with_no_terminal_state_is_untouched")]
    public async Task GoalWithNoTerminalStateIsUntouched()
    {
        var db = TempDb();
        var store = new SqliteRunEventStore(db);
        var now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        await AppendGoalOperationAsync(store, "active-goal", now.AddDays(-90));

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = ["different-terminal-goal"]
        });

        Assert.Equal(0, result.TerminalGoalOperationRowsDeleted);
        Assert.Single(await store.ReadSinceAsync(goalId: "active-goal"));
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_keeps_off_peak_classification_separate_from_daily_due_time")]
    public void RunEventMaintenanceCadenceKeepsOffPeakClassificationSeparateFromDailyDueTime()
    {
        Assert.True(RunEventMaintenanceCadence.IsOffPeakVacuumWindow(
            DateTimeOffset.Parse("2026-08-23T03:00:00Z")));
        Assert.False(RunEventMaintenanceCadence.IsOffPeakVacuumWindow(
            DateTimeOffset.Parse("2026-08-23T12:00:00Z")));
        Assert.False(RunEventMaintenanceCadence.IsOffPeakVacuumWindow(
            DateTimeOffset.Parse("2026-08-24T03:00:00Z")));
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-23T12:00:00Z"),
            RunEventMaintenanceCadence.NextDue(DateTimeOffset.Parse("2026-08-22T12:00:00Z")));
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_fresh_daily_marker_suppresses_inline_vacuum")]
    public async Task FreshDailyMarkerSuppressesInlineVacuum()
    {
        var root = CreateTempDirectory();
        var db = Path.Combine(root, "run-events.db");
        var logPath = Path.Combine(root, "logs", ConductEventLogWriter.CurrentFileName);
        var now = DateTimeOffset.Parse("2026-08-23T03:00:00Z");
        var store = new SqliteRunEventStore(db);
        await store.AppendAsync(new RunEventAppend(
            RunEventTypes.RunEventMaintenance,
            null,
            RunEventMaintenanceCadence.Operation,
            "Completed",
            "fresh daily marker",
            "{}",
            OccurredAt: now.AddHours(-1)));
        RunEventMaintenanceOptions? observedOptions = null;

        var result = RunEventMaintenanceCadence.TryRunIfDue(
            db,
            logPath,
            () => now,
            (_, options) =>
            {
                observedOptions = options;
                return new RunEventMaintenanceResult(
                    Deferred: false,
                    DeferredReason: null,
                    ConductorTickRowsDeleted: 0,
                    AgedConductorTickRowsDeleted: 0,
                    OversizedConductorTickRowsDeleted: 0,
                    DeletedPayloadBytesEstimate: 0,
                    MaxRowsDeletedInTransaction: 0,
                    Duration: TimeSpan.Zero,
                    BytesBefore: 1024,
                    BytesAfter: 512,
                    VacuumRequested: true,
                    VacuumCompleted: true,
                    VacuumDeferred: false);
            });

        Assert.True(result.Skipped);
        Assert.Null(observedOptions);
        Assert.Null(await store.ReadLatestAsync(
            RunEventTypes.RunEventMaintenance,
            RunEventMaintenanceCadence.VacuumOperation));
    }

    [Xunit.Fact]
    public async Task ArtifactRetentionReceipt_PersistsTypedPartialDecisions()
    {
        var root = CreateTempDirectory();
        var store = new SqliteRunEventStore(Path.Combine(root, "run-events.db"));
        var now = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var result = new StorageRetentionResult(
            WorkerArtifactsDeleted: 1,
            WorkerLogsCompressed: 0,
            SuccessfulTrxReceiptsWritten: 0,
            AcceptanceArtifactsDeleted: 0,
            PromptArtifactsDeleted: 0,
            GoalJournalsArchived: 0,
            SweepId: "sweep-1",
            Decisions:
            [
                new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.DispatchLogs,
                    EvidenceRetentionAction.Deleted,
                    "deleted.log",
                    "goal-1",
                    EvidenceOwnerResolution.UniqueTerminal,
                    "past-deletion-age",
                    BytesAttempted: 12,
                    BytesReclaimed: 12),
                new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.AcceptanceGateAttempts,
                    EvidenceRetentionAction.DeferredLocked,
                    "locked.trx",
                    "goal-1",
                    EvidenceOwnerResolution.UniqueTerminal,
                    "exclusive-delete-failed",
                    FailureExceptionType: nameof(IOException))
            ]);

        RunEventMaintenanceCadence.AppendArtifactRetentionReceipt(store, result, now);

        var receipt = Assert.IsType<RunEventRecord>(await store.ReadLatestAsync(
            RunEventTypes.EvidenceRetention,
            RunEventMaintenanceCadence.ArtifactRetentionOperation));
        Assert.Equal("Partial", receipt.Status);
        using var payload = JsonDocument.Parse(receipt.PayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("policyVersion").GetInt32());
        Assert.Equal(12, payload.RootElement.GetProperty("reclaimedBytes").GetInt64());
        Assert.False(payload.RootElement.GetProperty("decisionsTruncated").GetBoolean());
        Assert.Equal(2, payload.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Contains("actions=DeferredLocked:1,Deleted:1", receipt.Detail, StringComparison.Ordinal);
        Assert.Contains("reasons=exclusive-delete-failed:1,past-deletion-age:1", receipt.Detail, StringComparison.Ordinal);

        var failedResult = result with
        {
            SweepId = "sweep-2",
            Decisions =
            [
                result.Decisions[0],
                new EvidenceRetentionDecision(
                    EvidenceArtifactFamily.RunEvents,
                    EvidenceRetentionAction.Failed,
                    root,
                    null,
                    EvidenceOwnerResolution.Unrecorded,
                    "sweep-failed",
                    FailureExceptionType: nameof(IOException))
            ]
        };
        RunEventMaintenanceCadence.AppendArtifactRetentionReceipt(store, failedResult, now.AddSeconds(1));
        var failedReceipt = Assert.IsType<RunEventRecord>(await store.ReadLatestAsync(
            RunEventTypes.EvidenceRetention,
            RunEventMaintenanceCadence.ArtifactRetentionOperation));
        Assert.Equal("Failed", failedReceipt.Status);
        Assert.Contains("status=partial-failed", failedReceipt.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ArtifactRetentionReceiptRetainsEveryDestructiveDecisionPastNominalLimit()
    {
        var root = CreateTempDirectory();
        var store = new SqliteRunEventStore(Path.Combine(root, "run-events-destructive-retention.db"));
        var preserved = Enumerable.Range(0, 300)
            .Select(index => new EvidenceRetentionDecision(
                EvidenceArtifactFamily.AcceptanceGateAttempts,
                EvidenceRetentionAction.Preserved,
                $"preserved-{index:D3}.log",
                "goal-1",
                EvidenceOwnerResolution.UniqueTerminal,
                "preserved"));
        var deleted = new[]
        {
            new EvidenceRetentionDecision(
                EvidenceArtifactFamily.AcceptanceGateAttempts,
                EvidenceRetentionAction.Deleted,
                "deleted-late-1.log",
                "goal-1",
                EvidenceOwnerResolution.UniqueTerminal,
                "past-age-bound",
                BytesAttempted: 10,
                BytesReclaimed: 10),
            new EvidenceRetentionDecision(
                EvidenceArtifactFamily.AcceptanceGateAttempts,
                EvidenceRetentionAction.Deleted,
                "deleted-late-2.log",
                "goal-1",
                EvidenceOwnerResolution.UniqueTerminal,
                "past-count-bound",
                BytesAttempted: 20,
                BytesReclaimed: 20)
        };
        var result = new StorageRetentionResult(
            WorkerArtifactsDeleted: 0,
            WorkerLogsCompressed: 0,
            SuccessfulTrxReceiptsWritten: 0,
            AcceptanceArtifactsDeleted: 2,
            PromptArtifactsDeleted: 0,
            GoalJournalsArchived: 0,
            SweepId: "sweep-destructive-retention",
            Decisions: preserved.Concat(deleted).ToArray());

        RunEventMaintenanceCadence.AppendArtifactRetentionReceipt(store, result, DateTimeOffset.UtcNow);

        var receipt = Assert.IsType<RunEventRecord>(await store.ReadLatestAsync(
            RunEventTypes.EvidenceRetention,
            RunEventMaintenanceCadence.ArtifactRetentionOperation));
        using var payload = JsonDocument.Parse(receipt.PayloadJson);
        var paths = payload.RootElement.GetProperty("decisions")
            .EnumerateArray()
            .Select(item => item.GetProperty("Path").GetString())
            .ToArray();
        Assert.True(payload.RootElement.GetProperty("decisionsTruncated").GetBoolean());
        Assert.Equal(0, payload.RootElement.GetProperty("destructiveDecisionsDropped").GetInt32());
        Assert.Contains("deleted-late-1.log", paths);
        Assert.Contains("deleted-late-2.log", paths);

        var destructiveOverflow = Enumerable.Range(0, 300)
            .Select(index => new EvidenceRetentionDecision(
                EvidenceArtifactFamily.AcceptanceGateAttempts,
                EvidenceRetentionAction.Deleted,
                $"deleted-overflow-{index:D3}.log",
                "goal-1",
                EvidenceOwnerResolution.UniqueTerminal,
                "past-age-bound",
                BytesAttempted: 1,
                BytesReclaimed: 1))
            .ToArray();
        RunEventMaintenanceCadence.AppendArtifactRetentionReceipt(
            store,
            result with
            {
                SweepId = "sweep-destructive-overflow",
                AcceptanceArtifactsDeleted = destructiveOverflow.Length,
                Decisions = destructiveOverflow
            },
            DateTimeOffset.UtcNow.AddSeconds(1));
        var overflowReceipt = Assert.IsType<RunEventRecord>(await store.ReadLatestAsync(
            RunEventTypes.EvidenceRetention,
            RunEventMaintenanceCadence.ArtifactRetentionOperation));
        using var overflowPayload = JsonDocument.Parse(overflowReceipt.PayloadJson);
        Assert.Equal(256, overflowPayload.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(44, overflowPayload.RootElement.GetProperty("destructiveDecisionsDropped").GetInt32());
    }

    private static Task<RunEventRecord> AppendGoalOperationAsync(
        SqliteRunEventStore store,
        string goalId,
        DateTimeOffset occurredAt) =>
        store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goalId,
            "conductor:test",
            "Completed",
            "test",
            "{}",
            OccurredAt: occurredAt));

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
