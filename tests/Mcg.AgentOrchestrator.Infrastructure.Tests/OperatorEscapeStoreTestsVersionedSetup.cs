using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class OperatorEscapeStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "operator-escape-setup-" + Guid.NewGuid().ToString("N"));
    private string Db(string name = "current.db") => Path.Combine(root, name);

    [Fact]
    public void Setup_FreshDatabase_MatchesFrozenLegacySchemaWithOnlyVersionTableAdded()
    {
        SetupLegacy(Db("legacy.db"));
        SqliteOperatorEscapeStore.Setup(Db());
        using var legacy = Open(Db("legacy.db"), SqliteOpenMode.ReadOnly);
        using var current = Open(Db(), SqliteOpenMode.ReadOnly);
        var oldSchema = Schema(legacy);
        var newSchema = Schema(current);

        Assert.Equal(["escapes"], oldSchema.Where(row => row.Type == "table").Select(row => row.Name).ToArray());
        var added = Assert.Single(newSchema.Except(oldSchema));
        Assert.Equal("table", added.Type);
        Assert.Equal(StoreSchemaVersions.TableName, added.Name);
        Assert.Equal(oldSchema, newSchema.Where(row => row.Name != added.Name).ToArray());
        foreach (var table in oldSchema.Where(row => row.Type == "table"))
            Assert.Equal(Columns(legacy, table.Name), Columns(current, table.Name));
        Assert.Equal(["store_name", "version", "applied_at"], Columns(current, added.Name).Select(column => column.Name).ToArray());
        Assert.Equal(1, StoreSchemaVersions.Read(current, StoreSchemaRegistry.OperatorEscapes.StoreName));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("unversioned")]
    [InlineData("missing-record")]
    [InlineData("older")]
    [InlineData("newer")]
    public void ReadPaths_IncompatibleDatabase_RequireSetupWithoutWriting(string scenario)
    {
        var path = scenario == "missing-file" ? Db("absent/store.db") : Db();
        if (scenario == "unversioned")
            SetupLegacy(path);
        else if (scenario != "missing-file")
        {
            SqliteOperatorEscapeStore.Setup(path);
            using var connection = Open(path);
            if (scenario == "missing-record")
                RunNonQuery(connection, "DELETE FROM store_schema_versions");
            else
                RunNonQuery(connection, $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)}");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;

        Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => SqliteOperatorEscapeStore.OpenReadOnly(path)).Message);
        var store = new SqliteOperatorEscapeStore(path);
        if (scenario == "missing-file")
        {
            Assert.Empty(store.List());
            Assert.False(store.HasRecordSource("source"));
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
        {
            Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => store.List()).Message);
            Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => store.HasRecordSource("source")).Message);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void Setup_VersionRecordingFails_RollsBackSchemaChanges()
    {
        Directory.CreateDirectory(root);
        using (var connection = Open(Db(), SqliteOpenMode.ReadWriteCreate))
            RunNonQuery(connection, "CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY)");
        Assert.Throws<SqliteException>(() => SqliteOperatorEscapeStore.Setup(Db()));
        using var readBack = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Equal(["store_schema_versions"], Schema(readBack).Where(row => row.Type == "table").Select(row => row.Name).ToArray());
    }

    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenReadOnly_SetUpDatabase_ReturnsSameRowsWithoutChangingFile()
    {
        var writable = new SqliteOperatorEscapeStore(Db());
        Assert.True(writable.TryAppendEscape(Escape("later", "found-goal", At.AddMinutes(1)), "later-source"));
        Assert.True(writable.TryAppendEscape(Escape("earlier", null, At), "earlier-source"));
        var rows = writable.List();
        Assert.Equal(["earlier", "later"], rows.Select(row => row.Id).ToArray());
        Assert.Equal("found-goal", rows[1].FoundByGoalId);
        var bytes = File.ReadAllBytes(Db());
        var modified = File.GetLastWriteTimeUtc(Db());

        var readOnly = SqliteOperatorEscapeStore.OpenReadOnly(Db());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(rows),
            System.Text.Json.JsonSerializer.Serialize(readOnly.List()));
        Assert.True(readOnly.HasRecordSource("earlier-source"));
        Assert.True(readOnly.HasRecordSource("later-source"));
        Assert.False(readOnly.HasRecordSource("missing"));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Db()));
    }

    [Fact]
    public void AppendEscape_AbsentPath_RecordsVersionAndRow()
    {
        Assert.False(File.Exists(Db()));
        Assert.True(new SqliteOperatorEscapeStore(Db()).TryAppendEscape(Escape("first", null, At), "first-source"));
        using var connection = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.OperatorEscapes.StoreName));
        Assert.Equal("first", Assert.Single(SqliteOperatorEscapeStore.OpenReadOnly(Db()).List()).Id);
    }

    [Fact]
    public void OpenReadOnly_WriteAttempt_IsRejectedWithoutChangingFile()
    {
        var writable = new SqliteOperatorEscapeStore(Db());
        Assert.True(writable.TryAppendEscape(Escape("existing", null, At), "source"));
        var bytes = File.ReadAllBytes(Db());
        var modified = File.GetLastWriteTimeUtc(Db());
        var readOnly = SqliteOperatorEscapeStore.OpenReadOnly(Db());

        Assert.Throws<InvalidOperationException>(() => readOnly.TryAppendEscape(Escape("forbidden", null, At), "new-source"));
        Assert.Equal("existing", Assert.Single(readOnly.List()).Id);
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Db()));
    }

    private static OperatorEscape Escape(string id, string? foundBy, DateTimeOffset at) =>
        new(id, "goal", "Reason", [], foundBy, "operator", OperatorActorKind.Human, "cli", at);

    // Frozen pre-change DDL from HEAD 8f67b44481f8776f1d7bec0316b1c9c32195d691.
    // Statements are copied verbatim; only the connection construction is adapted.
    private static void SetupLegacy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        RunNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS escapes (
                id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                goal_id TEXT NOT NULL, reason TEXT NOT NULL, evidence_json TEXT NOT NULL,
                found_by_goal_id TEXT, actor TEXT NOT NULL, actor_kind TEXT NOT NULL,
                channel TEXT NOT NULL, recorded_at TEXT NOT NULL);
            """);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record SchemaRow(string Type, string Name, string Table, string? Sql);
    private sealed record ColumnRow(long Id, string Name, string Type, long NotNull, string? DefaultValue, long PrimaryKey);

    private static SchemaRow[] Schema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name";
        using var reader = command.ExecuteReader();
        var rows = new List<SchemaRow>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows.ToArray();
    }

    private static ColumnRow[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        using var reader = command.ExecuteReader();
        var rows = new List<ColumnRow>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
        return rows.ToArray();
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

