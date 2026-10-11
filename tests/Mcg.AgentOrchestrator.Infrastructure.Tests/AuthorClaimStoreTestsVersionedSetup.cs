using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class AuthorClaimStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "author-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "author-claims.db");
    private static readonly string[] LegacyColumns =
        ["identity", "claimed_at", "outcome", "intent_id", "item_json", "output"];
    private static readonly string[] ClaimColumns = [.. LegacyColumns, "attempt"];

    [Fact]
    public void Setup_FreshDatabase_CreatesDirectorySchemaAndVersion()
    {
        Assert.False(Directory.Exists(root));

        AuthorClaimStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "author-claims"));
        Assert.Contains(Schema(connection), row => row.StartsWith("table|author_claims|", StringComparison.Ordinal));
        Assert.Equal(ClaimColumns, Columns(connection));
    }

    [Fact]
    public void Setup_LegacyDatabase_PreservesAllColumnsAndStoreOperations()
    {
        SetupLegacy(includeAttempt: true);
        string[] schema;
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertClaim(connection, "owner-a", "owner-question", includeAttempt: true);
            InsertClaim(connection, "flight-b", "in-flight", includeAttempt: true);
            Assert.Null(StoreSchemaVersions.Read(connection, "author-claims"));
            schema = Schema(connection);
            Assert.DoesNotContain(schema, row => row.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
            Assert.Equal(2L, Scalar(connection, "SELECT count(*) FROM author_claims"));
            rows = ClaimRows(connection, ClaimColumns);
        }

        AuthorClaimStoreSetup.Setup(Db);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "author-claims"));
            var after = Schema(connection);
            var added = Assert.Single(after.Except(schema));
            Assert.StartsWith("table|store_schema_versions|", added);
            Assert.Equal(schema, after.Where(row => row != added).ToArray());
            Assert.Equal(rows, ClaimRows(connection, ClaimColumns));
        }
        var store = new ConductorAuthorClaimStore(Db);
        Assert.Equal(2, store.Attempt("owner-a"));
        store.Release("flight-b");
        using var released = Open(Db);
        Assert.Equal(0L, Scalar(released, "SELECT count(*) FROM author_claims WHERE identity = 'flight-b'"));
        Assert.Equal(2, store.Attempt("owner-a"));
    }

    [Fact]
    public void Setup_MissingAttempt_AddsDefaultAndPreservesLegacyColumns()
    {
        SetupLegacy(includeAttempt: false);
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertClaim(connection, "legacy-a", "owner-question", includeAttempt: false);
            Assert.Null(StoreSchemaVersions.Read(connection, "author-claims"));
            Assert.Equal(LegacyColumns, Columns(connection));
            Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM author_claims"));
            rows = ClaimRows(connection, LegacyColumns);
        }

        AuthorClaimStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(ClaimColumns, Columns(current));
        Assert.Equal(1L, Scalar(current, "SELECT attempt FROM author_claims WHERE identity = 'legacy-a'"));
        Assert.Equal(rows, ClaimRows(current, LegacyColumns));
        Assert.Equal(1, StoreSchemaVersions.Read(current, "author-claims"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_NewerVersion_PreservesContentAndRunnerReports99(bool missingSchema)
    {
        SetupLegacy(includeAttempt: false);
        string[] schema;
        string? rows;
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertClaim(connection, "future-a", "owner-question", includeAttempt: false);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.AuthorClaims);
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 99, applied_at = 'future'");
            if (missingSchema)
                RunNonQuery(connection, "DROP TABLE author_claims");
            else
                Assert.Equal(LegacyColumns, Columns(connection));
            schema = Schema(connection);
            rows = missingSchema ? null : ClaimRows(connection, LegacyColumns);
            versions = VersionRows(connection);
            Assert.Equal(99, StoreSchemaVersions.Read(connection, "author-claims"));
        }

        AuthorClaimStoreSetup.Setup(Db);

        AssertFutureContentUnchanged(schema, rows, versions);
        var result = Assert.Single(StoreSetupRunner.Run(root), result => result.StoreName == "author-claims");
        Assert.Equal(Db, result.DatabasePath);
        Assert.Equal(99, result.Version);
        AssertFutureContentUnchanged(schema, rows, versions);
    }

    [Fact]
    public void Setup_SecondCall_PreservesAppliedAtAndContent()
    {
        AuthorClaimStoreSetup.Setup(Db);
        string[] schema;
        string rows;
        string versions;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertClaim(connection, "current-a", "owner-question", includeAttempt: true);
            RunNonQuery(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
            schema = Schema(connection);
            rows = ClaimRows(connection, ClaimColumns);
            versions = VersionRows(connection);
        }

        AuthorClaimStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "author-claims"));
        Assert.Equal("original", Scalar(current,
            "SELECT applied_at FROM store_schema_versions WHERE store_name = 'author-claims'"));
        Assert.Equal(schema, Schema(current));
        Assert.Equal(rows, ClaimRows(current, ClaimColumns));
        Assert.Equal(versions, VersionRows(current));
    }

    private void AssertFutureContentUnchanged(string[] schema, string? rows, string versions)
    {
        using var connection = Open(Db);
        Assert.Equal(schema, Schema(connection));
        if (rows is not null)
        {
            Assert.Equal(LegacyColumns, Columns(connection));
            Assert.Equal(rows, ClaimRows(connection, LegacyColumns));
        }
        Assert.Equal(versions, VersionRows(connection));
        Assert.Equal(99, StoreSchemaVersions.Read(connection, "author-claims"));
    }

    // Frozen pre-attempt columns; the live shape appends attempt with the historical ALTER.
    private void SetupLegacy(bool includeAttempt)
    {
        Directory.CreateDirectory(root);
        using var connection = Open(Db, SqliteOpenMode.ReadWriteCreate);
        RunNonQuery(connection, """
            CREATE TABLE author_claims (
                identity TEXT PRIMARY KEY,
                claimed_at TEXT NOT NULL,
                outcome TEXT NOT NULL,
                intent_id TEXT NULL,
                item_json TEXT NOT NULL,
                output TEXT NULL
            )
            """);
        if (includeAttempt)
            RunNonQuery(connection, "ALTER TABLE author_claims ADD COLUMN attempt INTEGER NOT NULL DEFAULT 1");
    }

    private static void InsertClaim(SqliteConnection connection, string identity, string outcome, bool includeAttempt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = includeAttempt
            ? "INSERT INTO author_claims VALUES ($identity, $at, $outcome, $intent, $item, $output, $attempt)"
            : "INSERT INTO author_claims VALUES ($identity, $at, $outcome, $intent, $item, $output)";
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$at", "2026-10-01T12:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$intent", outcome == "owner-question" ? (object)"intent-'café'" : DBNull.Value);
        command.Parameters.AddWithValue("$item", "{\"text\":\"café 'quoted'\",\"lines\":\"first\\nsecond\"}");
        command.Parameters.AddWithValue("$output", outcome == "owner-question" ? (object)"Preserved 'output'\nwith café" : DBNull.Value);
        if (includeAttempt)
            command.Parameters.AddWithValue("$attempt", outcome == "owner-question" ? 2 : 1);
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
        command.CommandText = "PRAGMA table_info(author_claims)";
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

    private static string ClaimRows(SqliteConnection connection, string[] columns) =>
        RawRows(connection, "author_claims", columns, "identity");
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
