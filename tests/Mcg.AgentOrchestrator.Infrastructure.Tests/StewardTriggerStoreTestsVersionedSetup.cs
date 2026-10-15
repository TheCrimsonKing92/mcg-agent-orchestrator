using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class StewardTriggerStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "steward-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "steward-triggers.db");
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] TriggerColumns =
    [
        "key", "goal_id", "task_id", "candidate_sha", "kind", "trigger_json", "first_occurrence",
        "status", "claimed_at", "outcome", "intent_id", "repeat_question_raised"
    ];

    [Fact]
    public void Setup_FreshDatabase_CreatesDirectorySchemaAndVersion()
    {
        Assert.False(Directory.Exists(root));

        StewardTriggerStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "steward-triggers"));
        var schema = Schema(connection);
        Assert.Contains(schema, row => row.StartsWith("table|steward_triggers|", StringComparison.Ordinal));
        Assert.Contains(schema, row => row.StartsWith("table|steward_ready_rounds|", StringComparison.Ordinal));
        Assert.Contains(schema, row => row.StartsWith("index|steward_triggers_queue|", StringComparison.Ordinal));
    }

    [Fact]
    public void Setup_LegacyDatabase_PreservesAllColumnsAndStoreOperations()
    {
        SetupLegacy(Db);
        var serviced = Trigger("goal-a", "task-a", "sha-a");
        var pending = Trigger("goal-b", "task-b", "sha-b") with { OccurredAt = At.AddMinutes(1) };
        string[] schema;
        string triggers;
        string rounds;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertTrigger(connection, serviced, serviced: true);
            InsertTrigger(connection, pending, serviced: false);
            InsertReadyRound(connection, serviced);
            Assert.Null(StoreSchemaVersions.Read(connection, "steward-triggers"));
            schema = Schema(connection);
            Assert.DoesNotContain(schema, row => row.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
            triggers = TriggerRows(connection);
            rounds = ReadyRows(connection);
        }

        StewardTriggerStoreSetup.Setup(Db);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "steward-triggers"));
            var after = Schema(connection);
            var added = Assert.Single(after.Except(schema));
            Assert.StartsWith("table|store_schema_versions|", added);
            Assert.Equal(schema, after.Where(row => row != added).ToArray());
            Assert.Equal(triggers, TriggerRows(connection));
            Assert.Equal(rounds, ReadyRows(connection));
        }
        var store = new ConductorStewardTriggerStore(Db);
        var submitted = Assert.Single(store.SubmittedRoutes());
        Assert.Equal(ConductorStewardTriggerStore.KeyFor(serviced), submitted.Key);
        Assert.Equal("serviced", submitted.Status);
        Assert.Equal("route-submitted", submitted.Outcome);
        Assert.Equal("intent-a", submitted.IntentId);
        Assert.True(submitted.RepeatQuestionRaised);
        Assert.Equal(JsonSerializer.Serialize(serviced), JsonSerializer.Serialize(submitted.Trigger));
        var claimed = Assert.IsType<ConductorStewardStoredTrigger>(
            store.ClaimNext(At.AddMinutes(2), new HashSet<string> { pending.GoalId }));
        Assert.Equal(ConductorStewardTriggerStore.KeyFor(pending), claimed.Key);
        Assert.Equal("in-flight", claimed.Status);
        Assert.Equal(JsonSerializer.Serialize(pending), JsonSerializer.Serialize(claimed.Trigger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_NewerVersion_PreservesContentAndRunnerReports99(bool missingSchema)
    {
        SetupLegacy(Db);
        string[] schema;
        string triggers;
        string? rounds;
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            var trigger = Trigger("future-goal", "future-task", "future-sha");
            InsertTrigger(connection, trigger, serviced: true);
            InsertReadyRound(connection, trigger);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.StewardTriggers);
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 99, applied_at = 'future'");
            if (missingSchema)
                RunNonQuery(connection, "DROP TABLE steward_ready_rounds; DROP INDEX steward_triggers_queue;");
            schema = Schema(connection);
            triggers = TriggerRows(connection);
            rounds = missingSchema ? null : ReadyRows(connection);
            versions = VersionRows(connection);
        }

        StewardTriggerStoreSetup.Setup(Db);

        AssertFutureContentUnchanged(schema, triggers, rounds, versions);
        Assert.Contains(Db, Assert.Throws<InvalidOperationException>(() => StoreSetupRunner.Run(root)).Message);
        AssertFutureContentUnchanged(schema, triggers, rounds, versions);
    }

    [Fact]
    public void Setup_SecondCall_PreservesAppliedAtAndContent()
    {
        StewardTriggerStoreSetup.Setup(Db);
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            RunNonQuery(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
            versions = VersionRows(connection);
        }

        StewardTriggerStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "steward-triggers"));
        Assert.Equal(versions, VersionRows(current));
        using var command = current.CreateCommand();
        command.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'steward-triggers'";
        Assert.Equal("original", command.ExecuteScalar());
    }

    private void AssertFutureContentUnchanged(string[] schema, string triggers, string? rounds, string versions)
    {
        using var connection = Open(Db);
        Assert.Equal(schema, Schema(connection));
        Assert.Equal(triggers, TriggerRows(connection));
        if (rounds is not null)
            Assert.Equal(rounds, ReadyRows(connection));
        Assert.Equal(versions, VersionRows(connection));
        Assert.Equal(99, StoreSchemaVersions.Read(connection, "steward-triggers"));
    }

    private static ConductorStewardTrigger Trigger(string goal, string task, string sha) =>
        new(goal, task, sha, ConductorStewardTriggerKind.DeveloperGateReopenNoCommit, At,
            "Evidence with unicode: café\nsecond line", "WORKER_RESULT: fixture", ["receipt-a"], ["criterion-a"]);

    private static void InsertTrigger(SqliteConnection connection, ConductorStewardTrigger trigger, bool serviced)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO steward_triggers VALUES
            ($key, $goal, $task, $sha, $kind, $json, $occurred, $status, $claimed, $outcome, $intent, $repeat)
            """;
        command.Parameters.AddWithValue("$key", ConductorStewardTriggerStore.KeyFor(trigger));
        command.Parameters.AddWithValue("$goal", trigger.GoalId);
        command.Parameters.AddWithValue("$task", trigger.TaskId);
        command.Parameters.AddWithValue("$sha", trigger.CandidateSha);
        command.Parameters.AddWithValue("$kind", trigger.Kind.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(trigger));
        command.Parameters.AddWithValue("$occurred", trigger.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$status", serviced ? "serviced" : "pending");
        command.Parameters.AddWithValue("$claimed", serviced ? (object)At.AddMinutes(1).ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$outcome", serviced ? (object)"route-submitted" : DBNull.Value);
        command.Parameters.AddWithValue("$intent", serviced ? (object)"intent-a" : DBNull.Value);
        command.Parameters.AddWithValue("$repeat", serviced ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void InsertReadyRound(SqliteConnection connection, ConductorStewardTrigger trigger)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO steward_ready_rounds VALUES ($key, $output, 42)";
        command.Parameters.AddWithValue("$key", ConductorStewardTriggerStore.KeyFor(trigger));
        command.Parameters.AddWithValue("$output", "Preserved output\nwith 'quotes' and unicode: café");
        command.ExecuteNonQuery();
    }

    // Frozen pre-change DDL from HEAD 1251c6bda90c3afc0a5eb8d3632d94dfc5fd011e.
    private static void SetupLegacy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        RunNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS steward_triggers (
              key TEXT PRIMARY KEY, goal_id TEXT NOT NULL, task_id TEXT NOT NULL,
              candidate_sha TEXT NOT NULL, kind TEXT NOT NULL, trigger_json TEXT NOT NULL,
              first_occurrence TEXT NOT NULL, status TEXT NOT NULL, claimed_at TEXT,
              outcome TEXT, intent_id TEXT, repeat_question_raised INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS steward_triggers_queue ON steward_triggers(status, first_occurrence);
            CREATE TABLE IF NOT EXISTS steward_ready_rounds (
              key TEXT PRIMARY KEY, output TEXT NOT NULL, claimed_goal_version INTEGER NOT NULL);
            """);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string[] Schema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{(reader.IsDBNull(3) ? null : reader.GetString(3))}");
        return rows.ToArray();
    }

    private static string TriggerRows(SqliteConnection connection) => RawRows(connection, "steward_triggers", TriggerColumns, "key");
    private static string ReadyRows(SqliteConnection connection) =>
        RawRows(connection, "steward_ready_rounds", ["key", "output", "claimed_goal_version"], "key");
    private static string VersionRows(SqliteConnection connection) =>
        RawRows(connection, "store_schema_versions", ["store_name", "version", "applied_at"], "store_name");

    private static string RawRows(SqliteConnection connection, string table, string[] columns, string order)
    {
        using var command = connection.CreateCommand();
        var projection = string.Join(", ", columns.Select(column => $"typeof({column}) || ':' || quote({column})"));
        command.CommandText = $"SELECT {projection} FROM {table} ORDER BY {order}";
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray());
        return JsonSerializer.Serialize(rows);
    }

    private static void RunNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
