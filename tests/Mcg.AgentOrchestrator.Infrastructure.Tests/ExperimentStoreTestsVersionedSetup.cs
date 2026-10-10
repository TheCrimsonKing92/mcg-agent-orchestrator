using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns unique temporary databases and disables connection pooling.
public sealed class ExperimentStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "experiment-setup-" + Guid.NewGuid().ToString("N"));
    private string Db(string name = "current.db") => Path.Combine(root, name);

    [Theory]
    [InlineData("missing-file", StoreSchemaState.Missing)]
    [InlineData("unversioned", StoreSchemaState.Missing)]
    [InlineData("missing-record", StoreSchemaState.Missing)]
    [InlineData("older", StoreSchemaState.Older)]
    [InlineData("newer", StoreSchemaState.Newer)]
    public void OpenReadOnly_IncompatibleDatabase_RequiresSetupWithoutWriting(string scenario, StoreSchemaState state)
    {
        var path = scenario == "missing-file" ? Db("absent/store.db") : Db();
        if (scenario == "unversioned")
            SetupLegacy(path);
        else if (scenario != "missing-file")
        {
            ExperimentStore.Setup(path);
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            RunNonQuery(connection, scenario == "missing-record"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'experiments'"
                : $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)} WHERE store_name = 'experiments'");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => ExperimentStore.OpenReadOnly(path));

        Assert.Equal($"Experiment store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Setup_FreshDatabase_AddsOnlyVersionTableAndIsIdempotent()
    {
        SetupLegacy(Db("legacy.db"));
        Assert.False(File.Exists(Db()));

        ExperimentStore.Setup(Db());

        using (var legacy = Open(Db("legacy.db")))
        using (var current = Open(Db()))
        {
            AssertOnlyVersionTableAdded(Schema(legacy), Schema(current));
            Assert.Equal(1, StoreSchemaVersions.Read(current, StoreSchemaRegistry.Experiments.StoreName));
        }
        Assert.Empty(await ExperimentStore.OpenReadOnly(Db()).ListAllAsync());
        AssertSecondSetupUnchanged(Db());
    }

    [Fact]
    public async Task Setup_LegacyDatabase_PreservesEveryRowAndAddsOnlyVersionTable()
    {
        SetupLegacy(Db());
        string[] schema;
        string rows;
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
        {
            RunNonQuery(connection, """
                INSERT INTO experiments VALUES (
                    'first', 'First policy', NULL, '2026-10-01T12:00:00.0000000+00:00',
                    '{"kind":"policy","description":"First policy"}', '{"kind":"before-after-window"}',
                    '["rounds-per-landing"]',
                    '{"metric":"rounds-per-landing","breachIf":{"metric":"rounds-per-landing","op":">","changePercent":50}}',
                    '{"count":3,"unit":"gates"}', '{"keepIf":[],"revertIf":[]}', 'open', NULL);
                INSERT INTO experiments VALUES (
                    'second', 'Second policy', 'epic', '2026-10-01T12:01:00.0000000+00:00',
                    '{"kind":"policy","description":"Second policy"}', '{"kind":"before-after-window"}',
                    '["rounds-per-landing"]',
                    '{"metric":"rounds-per-landing","breachIf":{"metric":"rounds-per-landing","op":">","changePercent":50}}',
                    '{"count":3,"unit":"gates"}', '{"keepIf":[],"revertIf":[]}', 'confirmed',
                    '{"outcome":"confirmed","evidence":"receipt","action":"keep","decidedAt":"2026-10-01T12:02:00.0000000+00:00"}');
                """);
            schema = Schema(connection);
            rows = RawRows(connection, "SELECT * FROM experiments ORDER BY id");
        }

        ExperimentStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            AssertOnlyVersionTableAdded(schema, Schema(connection));
            Assert.Equal(rows, RawRows(connection, "SELECT * FROM experiments ORDER BY id"));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.Experiments.StoreName));
        }
        var bytes = File.ReadAllBytes(Db());
        var records = await ExperimentStore.OpenReadOnly(Db()).ListAllAsync();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        ExperimentRecord[] expected =
        [
            new("first", Spec("First policy"), at, ExperimentOutcomeState.Open, null),
            new("second", Spec("Second policy") with { EpicId = "epic" }, at.AddMinutes(1),
                ExperimentOutcomeState.Confirmed, new(ExperimentOutcomeState.Confirmed, "receipt", "keep", at.AddMinutes(2)))
        ];
        Assert.Equal(JsonSerializer.Serialize(expected, ExperimentStore.JsonOptions),
            JsonSerializer.Serialize(records, ExperimentStore.JsonOptions));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        AssertSecondSetupUnchanged(Db());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Setup_RecordedVersion_UpgradesOlderAndLeavesNewerUntouched(int version)
    {
        ExperimentStore.Setup(Db());
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
            RunNonQuery(connection, $"UPDATE store_schema_versions SET version = {version}, applied_at = 'original'");
        var bytes = File.ReadAllBytes(Db());

        ExperimentStore.Setup(Db());

        using var current = Open(Db());
        Assert.Equal(version == 0 ? 1 : 2, StoreSchemaVersions.Read(current, StoreSchemaRegistry.Experiments.StoreName));
        if (version == 2)
            Assert.Equal(bytes, File.ReadAllBytes(Db()));
    }

    [Fact]
    public void Setup_VersionRecordingFails_RollsBackSchemaChanges()
    {
        Directory.CreateDirectory(root);
        using (var connection = Open(Db(), SqliteOpenMode.ReadWriteCreate))
            RunNonQuery(connection, """
                CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL);
                CREATE TRIGGER reject_version BEFORE INSERT ON store_schema_versions BEGIN SELECT RAISE(ABORT, 'reject version'); END;
                """);
        string[] before;
        using (var connection = Open(Db()))
            before = Schema(connection);

        var error = Assert.Throws<SqliteException>(() => ExperimentStore.Setup(Db()));

        Assert.Contains("reject version", error.Message);
        using var current = Open(Db());
        Assert.Equal(before, Schema(current));
        Assert.Null(StoreSchemaVersions.Read(current, StoreSchemaRegistry.Experiments.StoreName));
    }

    [Fact]
    public async Task OpenReadOnly_WriteAttempt_IsRejectedWithoutChangingFile()
    {
        var writable = new ExperimentStore(Db());
        var record = await writable.AddAsync(Spec("Existing policy"));
        using (var connection = Open(Db()))
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.Experiments.StoreName));
        var bytes = File.ReadAllBytes(Db());
        var readOnly = ExperimentStore.OpenReadOnly(Db());

        var error = await Assert.ThrowsAsync<SqliteException>(() => readOnly.DecideAsync(
            record.Id, ExperimentOutcomeState.Confirmed, "receipt", "keep"));

        Assert.Equal(8, error.SqliteErrorCode); // SQLITE_READONLY
        Assert.Equal(ExperimentOutcomeState.Open, Assert.Single(await readOnly.ListAllAsync()).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
    }

    private static ExperimentSpec Spec(string hypothesis) => new(hypothesis,
        new(ExperimentInterventionKind.Policy, hypothesis), new(ExperimentBaselineKind.BeforeAfterWindow),
        ["rounds-per-landing"], new("rounds-per-landing", new("rounds-per-landing", ">", 50)),
        new(3, ExperimentStopUnit.Gates), new([], []));

    private static void AssertSecondSetupUnchanged(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string versions;
        using (var connection = Open(path))
            versions = RawRows(connection, "SELECT * FROM store_schema_versions ORDER BY store_name");

        ExperimentStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var current = Open(path);
        Assert.Equal(versions, RawRows(current, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    private static void AssertOnlyVersionTableAdded(string[] before, string[] after)
    {
        Assert.Contains(before, row => row.StartsWith("table|experiments|", StringComparison.Ordinal));
        var added = Assert.Single(after.Except(before));
        Assert.StartsWith($"table|{StoreSchemaVersions.TableName}|", added);
        Assert.Equal(before, after.Where(row => row != added).ToArray());
    }

    // Frozen pre-change DDL from HEAD 8d45e0899f7296a0e21159d715be2c5d61bf0172.
    private static void SetupLegacy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        RunNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS experiments (
                id TEXT PRIMARY KEY, hypothesis TEXT NOT NULL, epic_id TEXT, created_at TEXT NOT NULL,
                intervention_json TEXT NOT NULL, baseline_json TEXT NOT NULL, metrics_json TEXT NOT NULL,
                guardrail_json TEXT NOT NULL, stop_rule_json TEXT NOT NULL, decision_rule_json TEXT NOT NULL,
                outcome TEXT NOT NULL CHECK(outcome IN ('open','confirmed','refuted','inconclusive')), decision_json TEXT);
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

    private static string RawRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetString(index)).ToArray());
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
