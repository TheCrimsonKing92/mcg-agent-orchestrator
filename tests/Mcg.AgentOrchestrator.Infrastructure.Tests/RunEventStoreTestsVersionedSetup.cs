using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class RunEventStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "run-events-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "run-events.db");
    private static readonly string[] EventColumns =
        ["seq", "event_id", "occurred_at", "event_type", "goal_id", "operation", "status", "detail", "payload_json"];

    [Fact]
    public void Setup_FreshDatabase_CreatesSchemaVersionAndPragmas()
    {
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(Db));

        RunEventStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "run-events"));
        var schema = Schema(connection);
        Assert.Contains(schema, row => row.StartsWith("table|run_events|", StringComparison.Ordinal));
        foreach (var index in new[] { "ix_run_events_goal_seq", "ix_run_events_type_seq", "ix_run_events_type_operation_seq" })
            Assert.Contains(schema, row => row.StartsWith($"index|{index}|run_events|", StringComparison.Ordinal));
        Assert.Equal(EventColumns, Columns(connection));
        Assert.Equal(2L, Scalar(connection, "PRAGMA auto_vacuum"));
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
    }

    [Fact]
    public async Task Setup_UnversionedDatabase_PreservesEveryColumnAndReadBack()
    {
        SetupLegacy();
        string[] schema;
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertEvents(connection);
            Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM run_events"));
            Assert.Equal(0L, Scalar(connection, "PRAGMA auto_vacuum"));
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
            Assert.Null(StoreSchemaVersions.Read(connection, "run-events"));
            schema = Schema(connection);
            Assert.DoesNotContain(schema, row => row.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
            rows = EventRows(connection);
        }

        RunEventStoreSetup.Setup(Db);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "run-events"));
            Assert.Equal(rows, EventRows(connection));
            Assert.Equal(0L, Scalar(connection, "PRAGMA auto_vacuum"));
            var after = Schema(connection);
            var added = Assert.Single(after.Except(schema));
            Assert.StartsWith("table|store_schema_versions|", added);
            Assert.Equal(schema, after.Where(row => row != added).ToArray());
        }

        var events = await new SqliteRunEventStore(Db).ReadSinceAsync();
        Assert.Equal(new[]
        {
            new RunEventRecord(7, "legacy-a", DateTimeOffset.Parse("2026-10-11T12:00:00+00:00"),
                "goal.operation", "goal-a", "start", "succeeded", "Preserved café", "{\"value\":1}"),
            new RunEventRecord(11, "legacy-b", DateTimeOffset.Parse("2026-10-11T13:00:00+00:00"),
                "conductor.tick", null, "", null, "", null)
        }, events);
    }

    [Fact]
    public async Task Constructor_DefaultSetupAndAppend_RecordsVersion()
    {
        Assert.False(File.Exists(Db));
        var store = new SqliteRunEventStore(Db);

        var appended = await store.AppendAsync(new RunEventAppend(
            "goal.operation", "goal-a", "start", "succeeded", null, null,
            OccurredAt: DateTimeOffset.Parse("2026-10-11T12:00:00Z"), EventId: "new-event"));

        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "run-events"));
        Assert.Equal(appended, Assert.Single(await store.ReadSinceAsync()));
        Assert.Equal("new-event", appended.EventId);
    }

    [Fact]
    public void Constructor_EnsureSchemaFalse_LeavesDatabaseUnversioned()
    {
        SetupLegacy();
        string[] schema;
        using (var connection = Open(Db))
        {
            schema = Schema(connection);
            Assert.Null(StoreSchemaVersions.Read(connection, "run-events"));
        }

        _ = new SqliteRunEventStore(Db, ensureSchema: false);

        using var readBack = Open(Db);
        Assert.Null(StoreSchemaVersions.Read(readBack, "run-events"));
        Assert.Equal(schema, Schema(readBack));
        Assert.DoesNotContain(Schema(readBack), row => row.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_NewerVersion_PreservesContentAndRunnerReports99(bool missingSchema)
    {
        SetupLegacy();
        string[] schema;
        string? rows;
        string versions;
        string futureRows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertEvents(connection);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.RunEvents);
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 99, applied_at = 'future'");
            RunNonQuery(connection, "CREATE TABLE future_rows(value TEXT NOT NULL); INSERT INTO future_rows VALUES ('untouched café')");
            if (missingSchema)
                RunNonQuery(connection, "DROP TABLE run_events");
            schema = Schema(connection);
            rows = missingSchema ? null : EventRows(connection);
            versions = VersionRows(connection);
            futureRows = RawRows(connection, "future_rows", ["value"], "value");
            Assert.Equal(99, StoreSchemaVersions.Read(connection, "run-events"));
        }

        RunEventStoreSetup.Setup(Db);

        AssertFutureContentUnchanged(schema, rows, versions, futureRows);
        Assert.Contains(Db, Assert.Throws<InvalidOperationException>(() => StoreSetupRunner.Run(root)).Message);
        AssertFutureContentUnchanged(schema, rows, versions, futureRows);
    }

    [Fact]
    public void Setup_SecondCall_PreservesAppliedAtAndContent()
    {
        RunEventStoreSetup.Setup(Db);
        string[] schema;
        string rows;
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertEvents(connection);
            RunNonQuery(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
            schema = Schema(connection);
            rows = EventRows(connection);
            versions = VersionRows(connection);
        }

        RunEventStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "run-events"));
        Assert.Equal("original", Scalar(current, "SELECT applied_at FROM store_schema_versions WHERE store_name = 'run-events'"));
        Assert.Equal(schema, Schema(current));
        Assert.Equal(rows, EventRows(current));
        Assert.Equal(versions, VersionRows(current));
    }

    private void AssertFutureContentUnchanged(string[] schema, string? rows, string versions, string futureRows)
    {
        using var connection = Open(Db);
        Assert.Equal(schema, Schema(connection));
        if (rows is not null)
        {
            Assert.Equal(EventColumns, Columns(connection));
            Assert.Equal(rows, EventRows(connection));
        }
        else
            Assert.Empty(Columns(connection));
        Assert.Equal(versions, VersionRows(connection));
        Assert.Equal(futureRows, RawRows(connection, "future_rows", ["value"], "value"));
        Assert.Equal(99, StoreSchemaVersions.Read(connection, "run-events"));
        Assert.Equal(0L, Scalar(connection, "PRAGMA auto_vacuum"));
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
    }

    private void SetupLegacy()
    {
        Directory.CreateDirectory(root);
        using var connection = Open(Db, SqliteOpenMode.ReadWriteCreate);
        // Frozen live schema, built independently of setup under test; auto_vacuum stays 0.
        RunNonQuery(connection, "PRAGMA journal_mode=WAL");
        RunNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS run_events (
                seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id     TEXT NOT NULL UNIQUE,
                occurred_at  TEXT NOT NULL,
                event_type   TEXT NOT NULL,
                goal_id      TEXT NULL,
                operation    TEXT NULL,
                status       TEXT NULL,
                detail       TEXT NULL,
                payload_json TEXT NULL
            )
            """);
        RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_goal_seq ON run_events(goal_id, seq)");
        RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_type_seq ON run_events(event_type, seq)");
        RunNonQuery(connection, "CREATE INDEX IF NOT EXISTS ix_run_events_type_operation_seq ON run_events(event_type, operation, seq)");
    }

    private static void InsertEvents(SqliteConnection connection) => RunNonQuery(connection, """
        INSERT INTO run_events(seq, event_id, occurred_at, event_type, goal_id, operation, status, detail, payload_json)
        VALUES (7, 'legacy-a', '2026-10-11T12:00:00+00:00', 'goal.operation', 'goal-a', 'start', 'succeeded', 'Preserved café', '{"value":1}'),
               (11, 'legacy-b', '2026-10-11T13:00:00+00:00', 'conductor.tick', NULL, '', NULL, '', NULL)
        """);

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string[] Columns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(run_events)";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns.ToArray();
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

    private static string EventRows(SqliteConnection connection) =>
        RawRows(connection, "run_events", EventColumns, "seq");
    private static string VersionRows(SqliteConnection connection) =>
        RawRows(connection, "store_schema_versions", ["store_name", "version", "applied_at"], "store_name");

    private static string RawRows(SqliteConnection connection, string table, string[] columns, string order)
    {
        using var command = connection.CreateCommand();
        // hex(NULL) is empty text in SQLite; keep nulls distinct from empty strings.
        var projection = string.Join(", ", columns.Select(column =>
            $"CASE WHEN {column} IS NULL THEN NULL ELSE hex(CAST({column} AS BLOB)) END"));
        command.CommandText = $"SELECT {projection} FROM {table} ORDER BY {order}";
        using var reader = command.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetString(index)).ToArray());
        return JsonSerializer.Serialize(rows);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
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
