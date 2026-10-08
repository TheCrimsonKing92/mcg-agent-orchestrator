using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class OperatorLessonStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "operator-lesson-setup-" + Guid.NewGuid().ToString("N"));
    private string Db(string name = "current.db") => Path.Combine(root, name);

    [Fact]
    public void Setup_FreshDatabase_MatchesFrozenLegacySchemaWithOnlyVersionTableAdded()
    {
        SetupLegacy(Db("legacy.db"));
        SqliteOperatorLessonStore.Setup(Db());
        using var legacy = Open(Db("legacy.db"), SqliteOpenMode.ReadOnly);
        using var current = Open(Db(), SqliteOpenMode.ReadOnly);
        var oldSchema = Schema(legacy);
        var newSchema = Schema(current);

        Assert.Equal(["lesson_retirements", "lessons"], oldSchema.Where(row => row.Type == "table").Select(row => row.Name).ToArray());
        var added = Assert.Single(newSchema.Except(oldSchema));
        Assert.Equal("table", added.Type);
        Assert.Equal(StoreSchemaVersions.TableName, added.Name);
        Assert.Equal(oldSchema, newSchema.Where(row => row.Name != added.Name).ToArray());
        foreach (var table in oldSchema.Where(row => row.Type == "table"))
            Assert.Equal(Columns(legacy, table.Name), Columns(current, table.Name));
        Assert.Equal(["store_name", "version", "applied_at"], Columns(current, added.Name).Select(column => column.Name).ToArray());
        Assert.Equal(1, StoreSchemaVersions.Read(current, StoreSchemaRegistry.OperatorLessons.StoreName));
        Assert.Contains(Columns(current, "lessons"), column => column.Name == "until_goal_id");
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
            SqliteOperatorLessonStore.Setup(path);
            using var connection = Open(path);
            if (scenario == "missing-record")
                RunNonQuery(connection, "DELETE FROM store_schema_versions");
            else
                RunNonQuery(connection, $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)}");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;

        Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => SqliteOperatorLessonStore.OpenReadOnly(path)).Message);
        var store = new SqliteOperatorLessonStore(path);
        if (scenario == "missing-file")
        {
            Assert.Empty(store.List());
            Assert.False(store.HasRecordSource("source"));
            Assert.False(store.HasRetirementSource("retirement"));
            Assert.Equal(OperatorLessonRetireResult.UnknownLesson, store.TryAppendRetirement(
                "unknown", "retirement", "reason", [], "operator", OperatorActorKind.Human, "cli", At));
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
        {
            Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => store.List()).Message);
            Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => store.HasRecordSource("source")).Message);
            Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() => store.HasRetirementSource("retirement")).Message);
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
        Assert.Throws<SqliteException>(() => SqliteOperatorLessonStore.Setup(Db()));
        using var readBack = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Equal(["store_schema_versions"], Schema(readBack).Where(row => row.Type == "table").Select(row => row.Name).ToArray());
    }

    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenReadOnly_SetUpDatabase_ReturnsSameRowsWithoutChangingFile()
    {
        var writable = new SqliteOperatorLessonStore(Db());
        Assert.True(writable.TryAppendLesson(Lesson("active", "until-goal"), "active-source"));
        Assert.True(writable.TryAppendLesson(Lesson("retired"), "retired-source"));
        Assert.Equal(OperatorLessonRetireResult.Retired, writable.TryAppendRetirement(
            "retired", "retirement-source", "superseded", [], "operator",
            OperatorActorKind.Human, "cli", At.AddMinutes(1)));
        var active = writable.List();
        var all = writable.List(includeRetired: true);
        Assert.Equal("active", Assert.Single(active).Id);
        Assert.Equal("until-goal", active[0].UntilGoalId);
        Assert.Equal(2, all.Count);
        Assert.Equal("superseded", Assert.Single(all, row => row.Id == "retired").RetireReason);
        var bytes = File.ReadAllBytes(Db());
        var modified = File.GetLastWriteTimeUtc(Db());

        var readOnly = SqliteOperatorLessonStore.OpenReadOnly(Db());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(active),
            System.Text.Json.JsonSerializer.Serialize(readOnly.List()));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(all),
            System.Text.Json.JsonSerializer.Serialize(readOnly.List(includeRetired: true)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(writable.List(appliesTo: "author")),
            System.Text.Json.JsonSerializer.Serialize(readOnly.List(appliesTo: "author")));
        Assert.Empty(readOnly.List(appliesTo: "unmatched"));
        Assert.True(readOnly.HasRecordSource("active-source"));
        Assert.True(readOnly.HasRecordSource("retired-source"));
        Assert.False(readOnly.HasRecordSource("missing"));
        Assert.True(readOnly.HasRetirementSource("retirement-source"));
        Assert.False(readOnly.HasRetirementSource("missing"));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Db()));
    }

    [Fact]
    public void Setup_LegacyWithoutUntilGoal_PreservesRowsAndAddsColumnAndVersion()
    {
        SetupLegacy(Db(), includeUntilGoal: false);
        using (var legacy = Open(Db()))
        {
            RunNonQuery(legacy, """
                INSERT INTO lessons VALUES ('legacy', 'legacy-source', 'Old situation', 'Old rule',
                    '["author"]', '[]', 'operator', 'Human', 'cli', '2026-10-01T12:00:00+00:00', NULL);
                INSERT INTO lesson_retirements VALUES ('legacy', 'retirement-source', 'superseded',
                    '[]', 'operator', 'Human', 'cli', '2026-10-01T12:01:00+00:00');
                """);
        }
        SqliteOperatorLessonStore.Setup(Db());

        var row = Assert.Single(SqliteOperatorLessonStore.OpenReadOnly(Db()).List(includeRetired: true));
        Assert.Equal("legacy", row.Id);
        Assert.Equal("Old situation", row.Situation);
        Assert.Equal("Old rule", row.Rule);
        Assert.Equal(["author"], row.AppliesTo.ToArray());
        Assert.Null(row.UntilGoalId);
        Assert.Equal("superseded", row.RetireReason);
        using var connection = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Contains(Columns(connection, "lessons"), column => column.Name == "until_goal_id");
        Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.OperatorLessons.StoreName));
    }

    [Fact]
    public void AppendLesson_AbsentPath_RecordsVersionAndRow()
    {
        Assert.False(File.Exists(Db()));
        Assert.True(new SqliteOperatorLessonStore(Db()).TryAppendLesson(Lesson("first"), "first-source"));
        using var connection = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.OperatorLessons.StoreName));
        Assert.Equal("first", Assert.Single(SqliteOperatorLessonStore.OpenReadOnly(Db()).List()).Id);
    }

    [Fact]
    public void AppendRetirement_LegacyDatabase_RecordsVersionAndRetirement()
    {
        SetupLegacy(Db());
        using (var connection = Open(Db()))
            RunNonQuery(connection, """
                INSERT INTO lessons VALUES ('legacy', 'legacy-source', 'Old situation', 'Old rule',
                    '[]', '[]', 'operator', 'Human', 'cli', '2026-10-01T12:00:00+00:00', NULL, NULL);
                """);

        Assert.Equal(OperatorLessonRetireResult.Retired, new SqliteOperatorLessonStore(Db()).TryAppendRetirement(
            "legacy", "retirement-source", "superseded", [], "operator", OperatorActorKind.Human, "cli", At));
        using var readBack = Open(Db(), SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.OperatorLessons.StoreName));
        Assert.Equal("superseded", Assert.Single(SqliteOperatorLessonStore.OpenReadOnly(Db()).List(includeRetired: true)).RetireReason);
    }

    [Fact]
    public void OpenReadOnly_WriteAttempts_AreRejectedWithoutChangingFile()
    {
        var writable = new SqliteOperatorLessonStore(Db());
        Assert.True(writable.TryAppendLesson(Lesson("existing"), "source"));
        var bytes = File.ReadAllBytes(Db());
        var modified = File.GetLastWriteTimeUtc(Db());
        var readOnly = SqliteOperatorLessonStore.OpenReadOnly(Db());

        Assert.Throws<InvalidOperationException>(() => readOnly.TryAppendLesson(Lesson("forbidden"), "new-source"));
        Assert.Throws<InvalidOperationException>(() => readOnly.TryAppendRetirement(
            "existing", "retirement", "reason", [], "operator", OperatorActorKind.Human, "cli", At));
        Assert.Equal("existing", Assert.Single(readOnly.List()).Id);
        Assert.False(readOnly.HasRetirementSource("retirement"));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Db()));
    }

    private static OperatorLesson Lesson(string id, string? until = null) =>
        new(id, "Situation", "Rule", ["author"], [], "operator", OperatorActorKind.Human,
            "cli", At, "goal", null, null, null, null, until);

    // Frozen pre-change DDL from HEAD 8f67b44481f8776f1d7bec0316b1c9c32195d691.
    // Statements are copied verbatim; only the connection construction is adapted.
    private static void SetupLegacy(string path, bool includeUntilGoal = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        var sql = """
            CREATE TABLE IF NOT EXISTS lessons (
                id TEXT PRIMARY KEY, source_intent_id TEXT NOT NULL UNIQUE,
                situation TEXT NOT NULL, rule TEXT NOT NULL, applies_to_json TEXT NOT NULL,
                evidence_json TEXT NOT NULL, actor TEXT NOT NULL, actor_kind TEXT NOT NULL,
                channel TEXT NOT NULL, recorded_at TEXT NOT NULL, goal_id TEXT, until_goal_id TEXT);
            CREATE TABLE IF NOT EXISTS lesson_retirements (
                lesson_id TEXT PRIMARY KEY REFERENCES lessons(id), source_intent_id TEXT NOT NULL UNIQUE,
                reason TEXT NOT NULL, evidence_json TEXT NOT NULL, actor TEXT NOT NULL,
                actor_kind TEXT NOT NULL, channel TEXT NOT NULL, retired_at TEXT NOT NULL);
            """;
        // The earlier construction omitted this nullable column; build that shape directly.
        RunNonQuery(connection, includeUntilGoal ? sql :
            sql.Replace("goal_id TEXT, until_goal_id TEXT", "goal_id TEXT", StringComparison.Ordinal));
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
