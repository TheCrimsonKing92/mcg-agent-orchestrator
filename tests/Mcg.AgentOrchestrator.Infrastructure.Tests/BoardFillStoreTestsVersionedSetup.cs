using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class BoardFillStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-fill-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "board-fill.db");
    private static readonly string[] RoundColumns = ["round_id", "payload"];
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");

    [Fact]
    public void Setup_FreshDatabase_CreatesDirectorySchemaAndVersion()
    {
        Assert.False(Directory.Exists(root));

        BoardFillStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "board-fill"));
        Assert.Contains(Schema(connection), row => row.StartsWith("table|board_fill_rounds|", StringComparison.Ordinal));
        Assert.Equal(RoundColumns, Columns(connection));
    }

    [Fact]
    public void Setup_UnversionedDatabase_AdoptsInPlaceAndStoreOperationsWork()
    {
        SetupLegacy();
        var draft = Round("draft-a", "draft");
        var failed = Round("failed-b", "failed");
        var payloads = new[] { JsonSerializer.Serialize(draft), JsonSerializer.Serialize(failed) };
        var expectedRows = new[] { draft.Id, failed.Id }.Zip(payloads, (id, payload) =>
            new[] { Hex(id), Hex(payload) }).ToArray();
        string[] schema;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertRound(connection, draft.Id, payloads[0]);
            InsertRound(connection, failed.Id, payloads[1]);
            Assert.Null(StoreSchemaVersions.Read(connection, "board-fill"));
            schema = Schema(connection);
            Assert.DoesNotContain(schema, row => row.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
            Assert.Equal(JsonSerializer.Serialize(expectedRows), RoundRows(connection));
        }

        BoardFillStoreSetup.Setup(Db);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "board-fill"));
            var after = Schema(connection);
            var added = Assert.Single(after.Except(schema));
            Assert.StartsWith("table|store_schema_versions|", added);
            Assert.Equal(schema, after.Where(row => row != added).ToArray());
            Assert.Equal(RoundColumns, Columns(connection));
            Assert.Equal(JsonSerializer.Serialize(expectedRows), RoundRows(connection));
        }

        var store = new ConductorBoardFillDraftStore(Db);
        var rounds = store.ReadAll();
        Assert.Equal(new[] { draft.Id, failed.Id }, rounds.Select(round => round.Id));
        Assert.Equal(new[] { "draft", "failed" }, rounds.Select(round => round.Outcome));
        store.MarkReported(failed.Id);
        Assert.True(Assert.Single(store.ReadAll(), round => round.Id == failed.Id).Reported);
        Assert.False(Assert.Single(store.ReadAll(), round => round.Id == draft.Id).Reported);
    }

    [Fact]
    public void StoreWrite_Begin_RecordsVersionAndRead_AbsentFile_CreatesNothing()
    {
        var store = new ConductorBoardFillDraftStore(Db);
        Assert.False(Directory.Exists(root));
        Assert.Empty(store.ReadAll());
        Assert.False(File.Exists(Db));
        Assert.False(Directory.Exists(root));

        var round = store.Begin(BoardFillReadyItemSelectorTests.Item(), Now);

        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "board-fill"));
        Assert.Equal(round.Id, Assert.Single(store.ReadAll()).Id);
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
            InsertRound(connection, "future-a", JsonSerializer.Serialize(Round("future-a", "draft")));
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.BoardFill);
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 99, applied_at = 'future'");
            // Preserve unrelated rows even when the future schema has no board-fill table.
            RunNonQuery(connection, "CREATE TABLE future_rows(value TEXT NOT NULL); INSERT INTO future_rows VALUES ('untouched café')");
            if (missingSchema)
                RunNonQuery(connection, "DROP TABLE board_fill_rounds");
            schema = Schema(connection);
            rows = missingSchema ? null : RoundRows(connection);
            versions = VersionRows(connection);
            futureRows = RawRows(connection, "future_rows", ["value"], "value");
            Assert.Equal(99, StoreSchemaVersions.Read(connection, "board-fill"));
        }

        BoardFillStoreSetup.Setup(Db);

        AssertFutureContentUnchanged(schema, rows, versions, futureRows);
        Assert.Contains(Db, Assert.Throws<InvalidOperationException>(() => StoreSetupRunner.Run(root)).Message);
        AssertFutureContentUnchanged(schema, rows, versions, futureRows);
    }

    [Fact]
    public void Setup_SecondCall_PreservesAppliedAtAndContent()
    {
        BoardFillStoreSetup.Setup(Db);
        string[] schema;
        string rows;
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertRound(connection, "current-a", JsonSerializer.Serialize(Round("current-a", "draft")));
            RunNonQuery(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
            schema = Schema(connection);
            rows = RoundRows(connection);
            versions = VersionRows(connection);
        }

        BoardFillStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "board-fill"));
        Assert.Equal("original", Scalar(current, "SELECT applied_at FROM store_schema_versions WHERE store_name = 'board-fill'"));
        Assert.Equal(schema, Schema(current));
        Assert.Equal(rows, RoundRows(current));
        Assert.Equal(versions, VersionRows(current));
    }

    private void AssertFutureContentUnchanged(string[] schema, string? rows, string versions, string futureRows)
    {
        using var connection = Open(Db);
        Assert.Equal(schema, Schema(connection));
        if (rows is not null)
        {
            Assert.Equal(RoundColumns, Columns(connection));
            Assert.Equal(rows, RoundRows(connection));
        }
        else
            Assert.Empty(Columns(connection));
        Assert.Equal(versions, VersionRows(connection));
        Assert.Equal(futureRows, RawRows(connection, "future_rows", ["value"], "value"));
        Assert.Equal(99, StoreSchemaVersions.Read(connection, "board-fill"));
    }

    private void SetupLegacy()
    {
        Directory.CreateDirectory(root);
        using var connection = Open(Db, SqliteOpenMode.ReadWriteCreate);
        // Frozen live schema, built independently of setup under test.
        RunNonQuery(connection, "CREATE TABLE board_fill_rounds(round_id TEXT PRIMARY KEY, payload TEXT NOT NULL)");
    }

    private static BoardFillDraftRound Round(string id, string outcome) =>
        new(id, new string('a', 32), Now, null, "main-head", outcome, null, null, [], Now, Now,
            Failure: outcome == "failed" ? "Preserved 'failure'\nwith café" : null);

    private static void InsertRound(SqliteConnection connection, string id, string payload)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO board_fill_rounds(round_id, payload) VALUES ($id, $payload)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$payload", payload);
        Assert.Equal(1, command.ExecuteNonQuery());
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

    private static string[] Columns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(board_fill_rounds)";
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

    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    private static string RoundRows(SqliteConnection connection) =>
        RawRows(connection, "board_fill_rounds", RoundColumns, "round_id");
    private static string VersionRows(SqliteConnection connection) =>
        RawRows(connection, "store_schema_versions", ["store_name", "version", "applied_at"], "store_name");

    private static string RawRows(SqliteConnection connection, string table, string[] columns, string order)
    {
        using var command = connection.CreateCommand();
        var projection = string.Join(", ", columns.Select(column => $"hex(CAST({column} AS BLOB))"));
        command.CommandText = $"SELECT {projection} FROM {table} ORDER BY {order}";
        using var reader = command.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray());
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
