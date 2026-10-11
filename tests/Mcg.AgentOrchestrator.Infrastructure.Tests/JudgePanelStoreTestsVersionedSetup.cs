using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: private temporary databases and fixture workspaces, with connection pooling disabled.
public sealed class JudgePanelStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "judge-panel-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "judge-panel.db");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
    private static readonly PanelCaseKey Key = new("goal-a", "candidate-a", "base-a", "criteria-a",
        "trigger-a", "PreReviewEvidence", "Preserved 'packet'\nwith café");
    private static readonly PanelJudgeResult Result = new("judge-a", PanelJudgeOutcome.Valid, 0,
        "Preserved 'stdout'\nwith café", "stderr", "answer", "reason", "{\"tokens\":12}", "model-a");
    private sealed record SeedRow(string Table, string[] Columns, object[] Values);

    [Fact]
    public void Setup_FreshDatabase_CreatesDirectorySchemaIndexAndVersion()
    {
        Assert.False(Directory.Exists(root));

        JudgePanelStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "judge-panel"));
        var schema = Schema(connection);
        foreach (var row in LegacyRows())
        {
            Assert.Contains(schema, entry => entry.StartsWith($"table|{row.Table}|", StringComparison.Ordinal));
            Assert.Equal(row.Columns, Columns(connection, row.Table));
        }
        Assert.Contains("index|panel_queue|panel_cases|CREATE INDEX panel_queue ON panel_cases(status)", schema);
    }

    [Fact]
    public void Setup_UnversionedDatabase_AdoptsInPlaceAndStoreReadsWork()
    {
        SetupLegacy();
        string[] schema;
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertLegacyRows(connection);
            Assert.Null(StoreSchemaVersions.Read(connection, "judge-panel"));
            schema = Schema(connection);
            Assert.DoesNotContain(schema, entry => entry.StartsWith("table|store_schema_versions|", StringComparison.Ordinal));
            AssertLegacyRows(connection);
            rows = ContentRows(connection, includeVersions: false);
        }

        JudgePanelStoreSetup.Setup(Db);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "judge-panel"));
            var after = Schema(connection);
            var added = Assert.Single(after.Except(schema));
            Assert.StartsWith("table|store_schema_versions|", added);
            Assert.Equal(schema, after.Where(entry => entry != added).ToArray());
            AssertLegacyRows(connection);
            Assert.Equal(rows, ContentRows(connection, includeVersions: false));
        }

        var store = new ConductorJudgePanelCaseStore(Db);
        var item = Assert.Single(store.Cases());
        Assert.Equal(ConductorJudgePanelCaseStore.CaseId(Key), item.Id);
        Assert.Equal(Key, item.Key);
        Assert.Equal("terminal", item.Status);
        Assert.Equal(PanelCaseTerminal.Completed, item.Terminal);
        Assert.Equal(Result, Assert.Single(store.Results(item.Id)));
    }

    [Fact]
    public async Task StoreWrite_Start_RecordsVersionAndOwnerDigestAbsentReadCreatesNothing()
    {
        var store = new ConductorJudgePanelCaseStore(Db);
        Assert.False(Directory.Exists(root));

        store.Start(Now, []);

        using (var connection = Open(Db))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "judge-panel"));
            Assert.Equal(Now.ToUniversalTime().ToString("O"),
                Scalar(connection, "SELECT value FROM panel_meta WHERE key = 'first-start'"));
        }
        using var fixture = await OwnerDigestJudgePanelFixture.CreateAsync();
        var digest = CliOwnerDigestCommand.Read(fixture.Workspace,
            new OwnerDigestTestFixture.MutableClock(OwnerDigestTestFixture.End), out var goals,
            OwnerDigestJudgePanelFixture.Start, OwnerDigestTestFixture.End);
        Assert.False(File.Exists(fixture.PanelPath));
        Assert.Null(CliOwnerDigestJudgePanel.Read(fixture.Workspace, digest, goals));
        Assert.False(File.Exists(fixture.PanelPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_NewerVersion_PreservesContentAndRunnerReports99(bool missingSchema)
    {
        SetupLegacy();
        string[] schema;
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertLegacyRows(connection);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.JudgePanel);
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 99, applied_at = 'future'");
            RunNonQuery(connection, "CREATE TABLE future_rows(value TEXT NOT NULL); INSERT INTO future_rows VALUES ('untouched café')");
            if (missingSchema)
                RunNonQuery(connection, "DROP TABLE panel_cases");
            schema = Schema(connection);
            rows = ContentRows(connection);
            Assert.Equal(99, StoreSchemaVersions.Read(connection, "judge-panel"));
        }

        JudgePanelStoreSetup.Setup(Db);

        AssertFutureContentUnchanged(schema, rows);
        var result = Assert.Single(StoreSetupRunner.Run(root), result => result.StoreName == "judge-panel");
        Assert.Equal(Db, result.DatabasePath);
        Assert.Equal(99, result.Version);
        AssertFutureContentUnchanged(schema, rows);
    }

    [Fact]
    public void Setup_SecondCall_PreservesAppliedAtAndContent()
    {
        JudgePanelStoreSetup.Setup(Db);
        string[] schema;
        string rows;
        using (var connection = Open(Db, SqliteOpenMode.ReadWrite))
        {
            InsertLegacyRows(connection);
            RunNonQuery(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
            schema = Schema(connection);
            rows = ContentRows(connection);
        }

        JudgePanelStoreSetup.Setup(Db);

        using var current = Open(Db);
        Assert.Equal(1, StoreSchemaVersions.Read(current, "judge-panel"));
        Assert.Equal("original", Scalar(current, "SELECT applied_at FROM store_schema_versions WHERE store_name = 'judge-panel'"));
        Assert.Equal(schema, Schema(current));
        Assert.Equal(rows, ContentRows(current));
    }

    private void AssertFutureContentUnchanged(string[] schema, string rows)
    {
        using var connection = Open(Db);
        Assert.Equal(99, StoreSchemaVersions.Read(connection, "judge-panel"));
        Assert.Equal(schema, Schema(connection));
        Assert.Equal(rows, ContentRows(connection));
    }

    private void SetupLegacy()
    {
        Directory.CreateDirectory(root);
        using var connection = Open(Db, SqliteOpenMode.ReadWriteCreate);
        // Frozen live schema, built independently of setup under test.
        RunNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS panel_cases (
                case_id TEXT PRIMARY KEY, goal_id TEXT NOT NULL, candidate_sha TEXT NOT NULL,
                base_sha TEXT NOT NULL, criteria_version TEXT NOT NULL, trigger_id TEXT NOT NULL,
                key_json TEXT NOT NULL, packet_hash TEXT NOT NULL, enrollment_window INTEGER,
                admitted INTEGER NOT NULL, status TEXT NOT NULL, claim_token TEXT, claimed_at TEXT,
                terminal TEXT, reason TEXT, event_written INTEGER NOT NULL DEFAULT 0,
                UNIQUE(goal_id, candidate_sha, base_sha, criteria_version, trigger_id));
            CREATE INDEX IF NOT EXISTS panel_queue ON panel_cases(status);
            CREATE TABLE IF NOT EXISTS panel_calls (
                case_id TEXT NOT NULL, judge TEXT NOT NULL, launched INTEGER NOT NULL,
                outcome TEXT, result_json TEXT, PRIMARY KEY(case_id, judge));
            CREATE TABLE IF NOT EXISTS panel_judge_health (
                judge TEXT PRIMARY KEY, consecutive_failures INTEGER NOT NULL,
                state TEXT NOT NULL, suspended_case_id TEXT);
            CREATE TABLE IF NOT EXISTS panel_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS panel_source_cursors (source TEXT PRIMARY KEY, cursor_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS panel_source_retained (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS panel_goals (ordinal INTEGER PRIMARY KEY,
                goal_id TEXT NOT NULL UNIQUE, created_at TEXT NOT NULL);
            """);
    }

    private static SeedRow[] LegacyRows() =>
    [
        new("panel_cases", ["case_id", "goal_id", "candidate_sha", "base_sha", "criteria_version", "trigger_id",
            "key_json", "packet_hash", "enrollment_window", "admitted", "status", "claim_token", "claimed_at",
            "terminal", "reason", "event_written"],
            [ConductorJudgePanelCaseStore.CaseId(Key), Key.GoalId, Key.CandidateSha, Key.BaseSha,
                Key.CriteriaVersion, Key.TriggerId, JsonSerializer.Serialize(Key), "packet-hash", 3, 1,
                "terminal", "claim-token", Now.ToString("O"), "completed", "Preserved 'reason'\nwith café", 1]),
        new("panel_calls", ["case_id", "judge", "launched", "outcome", "result_json"],
            [ConductorJudgePanelCaseStore.CaseId(Key), Result.Judge, 1, "valid", JsonSerializer.Serialize(Result)]),
        new("panel_judge_health", ["judge", "consecutive_failures", "state", "suspended_case_id"],
            [Result.Judge, 2, "suspended", "suspended-case"]),
        new("panel_meta", ["key", "value"], ["idle-reason", "Preserved 'idle'\nwith café"]),
        new("panel_source_cursors", ["source", "cursor_json"], ["source-a", "{\"cursor\":17}"]),
        new("panel_source_retained", ["key", "value"], ["retained-a", "Preserved 'retained'\nwith café"]),
        new("panel_goals", ["ordinal", "goal_id", "created_at"], [7, Key.GoalId, Now.ToString("O")])
    ];

    private static void InsertLegacyRows(SqliteConnection connection)
    {
        foreach (var row in LegacyRows())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {row.Table} ({string.Join(", ", row.Columns)}) " +
                $"VALUES ({string.Join(", ", row.Columns.Select((_, index) => "$p" + index))})";
            for (var index = 0; index < row.Values.Length; index++)
                command.Parameters.AddWithValue("$p" + index, row.Values[index]);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
    }

    private static void AssertLegacyRows(SqliteConnection connection)
    {
        foreach (var row in LegacyRows())
        {
            Assert.Equal(row.Columns, Columns(connection, row.Table));
            var expected = row.Values.Select(value =>
                (value is int ? "integer:" : "text:") + Convert.ToHexString(Encoding.UTF8.GetBytes(
                    Convert.ToString(value, CultureInfo.InvariantCulture)!))).ToArray();
            Assert.Equal(JsonSerializer.Serialize(new[] { expected }), RawRows(connection, row.Table, row.Columns));
        }
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

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
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

    private static string ContentRows(SqliteConnection connection, bool includeVersions = true)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }
        var rows = tables.Where(table => includeVersions || table != StoreSchemaVersions.TableName)
            .ToDictionary(table => table, table => RawRows(connection, table, Columns(connection, table)));
        return JsonSerializer.Serialize(rows);
    }

    private static string RawRows(SqliteConnection connection, string table, string[] columns)
    {
        using var command = connection.CreateCommand();
        var projection = string.Join(", ", columns.Select(column => $"typeof({column}) || ':' || hex(CAST({column} AS BLOB))"));
        command.CommandText = $"SELECT {projection} FROM {table} ORDER BY {string.Join(", ", columns)}";
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
