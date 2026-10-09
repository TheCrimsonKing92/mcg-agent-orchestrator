using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

// Parallel-safe: every test owns a private workspace and all SQLite connections disable pooling.
public sealed class ProgressiveReviewSteeringStoreVersionedSetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupLegacyOrOlderPreservesRowsAndIsIdempotent(bool older)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var expected = CreateLegacySteeringDatabase(path);
        using (var legacy = OpenConnection(path, SqliteOpenMode.ReadWrite))
        {
            Assert.Null(StoreSchemaVersions.Read(legacy, StoreSchemaRegistry.ProgressiveReviewSteering.StoreName));
            if (older)
            {
                StoreSchemaVersions.UpgradeToCurrent(legacy, StoreSchemaRegistry.ProgressiveReviewSteering);
                RunSql(legacy, "UPDATE store_schema_versions SET version = 0");
            }
        }

        ProgressiveReviewSteeringStoreSetup.Setup(path);

        AssertReceiptsEqual(expected, await SqliteProgressiveReviewSteeringStore.OpenReadOnly(path).ListReceiptsAsync());
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.ProgressiveReviewSteering.StoreName));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM progressive_review_steer_intents";
            Assert.Equal(2L, command.ExecuteScalar());
            command.CommandText = "SELECT status, completed_at FROM progressive_review_steer_intents ORDER BY id";
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("Pending", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.Read());
                Assert.Equal("Completed", reader.GetString(0));
                Assert.Equal("2026-07-02T12:00:00Z", reader.GetString(1));
                Assert.False(reader.Read());
            }
            RunSql(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
        }
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        ProgressiveReviewSteeringStoreSetup.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        AssertReceiptsEqual(expected, await SqliteProgressiveReviewSteeringStore.OpenReadOnly(path).ListReceiptsAsync());
    }

    [Fact]
    public void SetupNewerVersionLeavesBytesSchemaAndJournalModeUntouched()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        CreateLegacySteeringDatabase(path);
        ProgressiveReviewSteeringStoreSetup.Setup(path);
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWrite))
            RunSql(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'future'");
        var journalMode = JournalMode(path);
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        ProgressiveReviewSteeringStoreSetup.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        Assert.Equal(journalMode, JournalMode(path));
        using var readBack = OpenConnection(path);
        Assert.Equal(2, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.ProgressiveReviewSteering.StoreName));
    }

    [Theory]
    [InlineData("missing", StoreSchemaState.Missing)]
    [InlineData("unversioned", StoreSchemaState.Missing)]
    [InlineData("missing-record", StoreSchemaState.Missing)]
    [InlineData("older", StoreSchemaState.Older)]
    [InlineData("newer", StoreSchemaState.Newer)]
    public void OpenReadOnlyRequiresCurrentSchemaWithoutMutation(string scenario, StoreSchemaState state)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.RootDirectory, "absent-directory", "progressive-review-steering.db");
        if (scenario == "unversioned")
            CreateLegacySteeringDatabase(path);
        else if (scenario != "missing")
        {
            ProgressiveReviewSteeringStoreSetup.Setup(path);
            using var connection = OpenConnection(path, SqliteOpenMode.ReadWrite);
            RunSql(connection, scenario switch
            {
                "missing-record" => "DELETE FROM store_schema_versions",
                "older" => "UPDATE store_schema_versions SET version = 0",
                _ => "UPDATE store_schema_versions SET version = 2"
            });
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var schema = File.Exists(path) ? SchemaSnapshot(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => SqliteProgressiveReviewSteeringStore.OpenReadOnly(path));

        Assert.Equal($"Progressive review steering store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(schema, SchemaSnapshot(path));
        }
    }

    [Fact]
    public async Task OpenReadOnlyReturnsWriterRowsAndRejectsAllWrites()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var writer = SqliteProgressiveReviewSteeringStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var time = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        var intent = new ProgressiveReviewSteerIntent("writer-intent", "goal", "task", "Developer", "round",
            "glance", "hash", time, "evidence", "direction", "guidance", time);
        await writer.EnqueueIntentAsync(intent);
        var expected = ExpectedReceipts();
        foreach (var receipt in expected.Reverse())
            await writer.AppendReceiptAsync(receipt with { IntentId = intent.Id });
        expected = expected.Select(receipt => receipt with { IntentId = intent.Id }).ToArray();
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        var reader = SqliteProgressiveReviewSteeringStore.OpenReadOnly(path);
        AssertReceiptsEqual(expected, await reader.ListReceiptsAsync());
        Assert.Equal(2, await reader.CountReceiptsForRoundAsync("round"));
        Assert.Equal(1, await reader.CountReceiptsForRoundAsync("other-round"));
        Assert.Equal(0, await reader.CountReceiptsForRoundAsync("absent-round"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));

        var enqueue = await Assert.ThrowsAsync<SqliteException>(() => reader.EnqueueIntentAsync(intent with { Id = "forbidden" }));
        Assert.Equal(8, enqueue.SqliteErrorCode);
        var complete = await Assert.ThrowsAsync<SqliteException>(() => reader.CompleteIntentAsync(intent.Id, time.AddMinutes(1)));
        Assert.Equal(8, complete.SqliteErrorCode);
        var append = await Assert.ThrowsAsync<SqliteException>(() => reader.AppendReceiptAsync(expected[0] with { Id = "forbidden" }));
        Assert.Equal(8, append.SqliteErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReserveNextPendingAsync("goal"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        AssertReceiptsEqual(expected, await reader.ListReceiptsAsync());

        // Removing the file proves the writer guard precedes even an attempted connection open.
        File.Delete(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReserveNextPendingAsync("absent-goal"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task SetupAndReadOnlyReadsPreserveLegacyJournalMode()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var expected = CreateLegacySteeringDatabase(path);
        var journalMode = JournalMode(path);
        for (var run = 0; run < 2; run++)
        {
            ProgressiveReviewSteeringStoreSetup.Setup(path);
            Assert.Equal(journalMode, JournalMode(path));
            var reader = SqliteProgressiveReviewSteeringStore.OpenReadOnly(path);
            AssertReceiptsEqual(expected, await reader.ListReceiptsAsync());
            Assert.Equal(2, await reader.CountReceiptsForRoundAsync("round"));
            Assert.Equal(journalMode, JournalMode(path));
        }
    }

    [Fact]
    public void SetupVersionFailureRollsBackAllTablesAndIndexes()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate))
            RunSql(connection, "CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER)");

        Assert.Throws<SqliteException>(() => ProgressiveReviewSteeringStoreSetup.Setup(path));

        using var readBack = OpenConnection(path);
        using var command = readBack.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE name IN (
                'progressive_review_steer_intents', 'progressive_review_steer_receipts',
                'idx_progressive_review_steer_intents_pending', 'idx_progressive_review_steer_intents_round',
                'idx_progressive_review_steer_receipts_round')
            """;
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Null(StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.ProgressiveReviewSteering.StoreName));
    }

    private static string DatabasePath(ConductorVerbStartTests.Fixture fixture) =>
        Path.Combine(fixture.Workspace.OrchestratorDirectory, "progressive-review-steering.db");

    private static ProgressiveReviewSteerReceipt[] ExpectedReceipts()
    {
        var time = DateTimeOffset.Parse("2026-07-01T12:00:00Z");
        var first = new ProgressiveReviewSteerReceipt("receipt-a", "intent-1", "goal-1", "task-1", "round",
            "glance-1", "hash-1", "evidence-1", "confirmed-1", "admitted-1", ["check-1", "check-2"],
            "guidance-1", 11, 12, 13, 14, 15, 16, "outcome-1", time);
        return [first, first with { Id = "receipt-b", IntentId = "intent-2", GoalId = "goal-2", TaskId = "task-2",
            TriggerGlanceId = "glance-2", InputsHash = "hash-2", MisdirectionEvidence = "evidence-2",
            CancelConfirmation = "confirmed-2", Decision = "admitted-2", AdmissionChecks = [], GuidanceText = "guidance-2",
            CancelledInputTokens = 21, CancelledOutputTokens = 22, SteeredInputTokens = 23, SteeredOutputTokens = 24,
            CancelledWallMilliseconds = 25, SteeredWallMilliseconds = 26, Outcome = "outcome-2" },
            first with { Id = "receipt-c", RoundKey = "other-round", CreatedAt = time.AddDays(1) }];
    }

    private static ProgressiveReviewSteerReceipt[] CreateLegacySteeringDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        // Frozen pre-versioning DDL: no call to current setup and no journal-mode setting.
        RunSql(conn, """
            CREATE TABLE IF NOT EXISTS progressive_review_steer_intents (
                id                        TEXT PRIMARY KEY,
                goal_id                   TEXT NOT NULL,
                task_id                   TEXT NOT NULL,
                role                      TEXT NOT NULL,
                round_key                 TEXT NOT NULL,
                trigger_glance_id         TEXT NOT NULL,
                inputs_hash               TEXT NOT NULL,
                glance_verdict_timestamp  TEXT NOT NULL,
                misdirection_evidence     TEXT NOT NULL,
                corrective_direction      TEXT NOT NULL,
                guidance_text             TEXT NOT NULL,
                created_at                TEXT NOT NULL,
                status                    TEXT NOT NULL,
                completed_at              TEXT
            )
            """);
        RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_intents_pending ON progressive_review_steer_intents(goal_id, status, created_at)");
        RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_intents_round ON progressive_review_steer_intents(round_key)");
        RunSql(conn, """
            CREATE TABLE IF NOT EXISTS progressive_review_steer_receipts (
                id                         TEXT PRIMARY KEY,
                intent_id                  TEXT NOT NULL,
                goal_id                    TEXT NOT NULL,
                task_id                    TEXT NOT NULL,
                round_key                  TEXT NOT NULL,
                trigger_glance_id          TEXT NOT NULL,
                inputs_hash                TEXT NOT NULL,
                misdirection_evidence      TEXT NOT NULL,
                cancel_confirmation        TEXT NOT NULL,
                decision                   TEXT NOT NULL,
                admission_checks_json      TEXT NOT NULL,
                guidance_text              TEXT NOT NULL,
                cancelled_input_tokens     INTEGER NOT NULL,
                cancelled_output_tokens    INTEGER NOT NULL,
                steered_input_tokens       INTEGER NOT NULL,
                steered_output_tokens      INTEGER NOT NULL,
                cancelled_wall_ms          INTEGER NOT NULL,
                steered_wall_ms            INTEGER NOT NULL,
                outcome                    TEXT NOT NULL,
                created_at                 TEXT NOT NULL
            )
            """);
        RunSql(conn, "CREATE INDEX IF NOT EXISTS idx_progressive_review_steer_receipts_round ON progressive_review_steer_receipts(round_key)");
        RunSql(conn, """
            INSERT INTO progressive_review_steer_intents VALUES
                ('intent-1', 'goal-1', 'task-1', 'Developer', 'round', 'glance-1', 'hash-1',
                 '2026-07-01T12:00:00Z', 'evidence-1', 'direction-1', 'guidance-1', '2026-07-01T12:00:00Z', 'Pending', NULL),
                ('intent-2', 'goal-2', 'task-2', 'Reviewer', 'round', 'glance-2', 'hash-2',
                 '2026-07-02T12:00:00Z', 'evidence-2', 'direction-2', 'guidance-2', '2026-07-02T12:00:00Z', 'Completed', '2026-07-02T12:00:00Z')
            """);
        var receipts = ExpectedReceipts();
        foreach (var receipt in receipts.Reverse())
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT INTO progressive_review_steer_receipts (
                    id, intent_id, goal_id, task_id, round_key, trigger_glance_id, inputs_hash,
                    misdirection_evidence, cancel_confirmation, decision, admission_checks_json,
                    guidance_text, cancelled_input_tokens, cancelled_output_tokens, steered_input_tokens,
                    steered_output_tokens, cancelled_wall_ms, steered_wall_ms, outcome, created_at)
                VALUES ($id, $intent, $goal, $task, $round, $glance, $hash, $evidence, $cancel,
                    $decision, $checks, $guidance, $cancelIn, $cancelOut, $steerIn, $steerOut,
                    $cancelWall, $steerWall, $outcome, $created)
                """;
            command.Parameters.AddWithValue("$id", receipt.Id);
            command.Parameters.AddWithValue("$intent", receipt.IntentId);
            command.Parameters.AddWithValue("$goal", receipt.GoalId);
            command.Parameters.AddWithValue("$task", receipt.TaskId);
            command.Parameters.AddWithValue("$round", receipt.RoundKey);
            command.Parameters.AddWithValue("$glance", receipt.TriggerGlanceId);
            command.Parameters.AddWithValue("$hash", receipt.InputsHash);
            command.Parameters.AddWithValue("$evidence", receipt.MisdirectionEvidence);
            command.Parameters.AddWithValue("$cancel", receipt.CancelConfirmation);
            command.Parameters.AddWithValue("$decision", receipt.Decision);
            command.Parameters.AddWithValue("$checks", JsonSerializer.Serialize(receipt.AdmissionChecks));
            command.Parameters.AddWithValue("$guidance", receipt.GuidanceText);
            command.Parameters.AddWithValue("$cancelIn", receipt.CancelledInputTokens);
            command.Parameters.AddWithValue("$cancelOut", receipt.CancelledOutputTokens);
            command.Parameters.AddWithValue("$steerIn", receipt.SteeredInputTokens);
            command.Parameters.AddWithValue("$steerOut", receipt.SteeredOutputTokens);
            command.Parameters.AddWithValue("$cancelWall", receipt.CancelledWallMilliseconds);
            command.Parameters.AddWithValue("$steerWall", receipt.SteeredWallMilliseconds);
            command.Parameters.AddWithValue("$outcome", receipt.Outcome);
            command.Parameters.AddWithValue("$created", receipt.CreatedAt.ToString("O"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        return receipts;
    }

    private static void AssertReceiptsEqual(IReadOnlyList<ProgressiveReviewSteerReceipt> expected,
        IReadOnlyList<ProgressiveReviewSteerReceipt> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.IntentId, right.IntentId);
            Assert.Equal(left.GoalId, right.GoalId);
            Assert.Equal(left.TaskId, right.TaskId);
            Assert.Equal(left.RoundKey, right.RoundKey);
            Assert.Equal(left.TriggerGlanceId, right.TriggerGlanceId);
            Assert.Equal(left.InputsHash, right.InputsHash);
            Assert.Equal(left.MisdirectionEvidence, right.MisdirectionEvidence);
            Assert.Equal(left.CancelConfirmation, right.CancelConfirmation);
            Assert.Equal(left.Decision, right.Decision);
            Assert.Equal<string>(left.AdmissionChecks, right.AdmissionChecks);
            Assert.Equal(left.GuidanceText, right.GuidanceText);
            Assert.Equal(left.CancelledInputTokens, right.CancelledInputTokens);
            Assert.Equal(left.CancelledOutputTokens, right.CancelledOutputTokens);
            Assert.Equal(left.SteeredInputTokens, right.SteeredInputTokens);
            Assert.Equal(left.SteeredOutputTokens, right.SteeredOutputTokens);
            Assert.Equal(left.CancelledWallMilliseconds, right.CancelledWallMilliseconds);
            Assert.Equal(left.SteeredWallMilliseconds, right.SteeredWallMilliseconds);
            Assert.Equal(left.Outcome, right.Outcome);
            Assert.Equal(left.CreatedAt, right.CreatedAt);
        }
    }

    private static string[] SchemaSnapshot(string path)
    {
        using var connection = OpenConnection(path);
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

    private static string JournalMode(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void RunSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
