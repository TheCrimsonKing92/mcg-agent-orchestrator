using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns a unique temporary directory and disables connection pooling.
public sealed class ProgressiveReviewGlanceCircuitStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "glance-circuit-setup-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] OriginalTables =
        ["progressive_review_glance_circuits", "progressive_review_glance_suppressions"];
    private static readonly ProgressiveReviewGlanceContractIdentity Identity =
        new("provider", "profile", "model", "fingerprint", "json", "parser-v1");
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
            SqliteProgressiveReviewGlanceCircuitStore.Setup(path);
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            RunNonQuery(connection, scenario == "missing-record"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'progressive-review-glance-circuit'"
                : $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)} WHERE store_name = 'progressive-review-glance-circuit'");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => SqliteProgressiveReviewGlanceCircuitStore.OpenReadOnly(path));

        Assert.Equal($"Progressive review glance circuit store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void OpenReadOnly_WriteAttempt_IsRejectedWithoutChangingFile()
    {
        _ = new SqliteProgressiveReviewGlanceCircuitStore(Db());
        var bytes = File.ReadAllBytes(Db());
        var readOnly = SqliteProgressiveReviewGlanceCircuitStore.OpenReadOnly(Db());
        Assert.NotNull(readOnly);

        Assert.Throws<SqliteException>(() => readOnly.TryAcquireProbe(
            Identity, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1)));

        Assert.Equal(bytes, File.ReadAllBytes(Db()));
    }

    [Fact]
    public void Setup_FreshDatabase_CreatesBothTablesAndVersionOne()
    {
        Assert.False(File.Exists(Db()));

        SqliteProgressiveReviewGlanceCircuitStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables.Concat(["store_schema_versions"]), Tables(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName));
        }
        AssertSecondSetupUnchanged(Db());
    }

    [Fact]
    public void Setup_OldShapeDatabase_RecordsVersionAndPreservesRows()
    {
        SetupLegacy(Db());
        string rows;
        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables, Tables(connection));
            Assert.Null(StoreSchemaVersions.Read(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName));
            rows = OriginalRows(connection);
        }

        SqliteProgressiveReviewGlanceCircuitStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(rows, OriginalRows(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName));
        }
        AssertSecondSetupUnchanged(Db());

        var store = new SqliteProgressiveReviewGlanceCircuitStore(Db());
        var admission = store.TryAcquireProbe(Identity, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1));
        Assert.Equal(ProgressiveReviewGlanceCircuitAdmissionKind.SuppressedOpen, admission.Kind);
        Assert.Equal(Identity.CircuitIdentity, admission.CircuitIdentity);
        Assert.Equal("OutputContract", admission.OpeningCause);
        Assert.Equal("preserved-reason", admission.OpeningReason);
        Assert.Equal(8, admission.SuppressionCount);
        var aggregate = Assert.Single(store.DrainInactiveSuppressions(new HashSet<string>()));
        Assert.Equal(new ProgressiveReviewGlanceSuppressionAggregate(
            "round", "goal", "task", Identity.CircuitIdentity, "SuppressedOpen", "OutputContract",
            "preserved-reason", 3, 120, 45, 2, 1, "Rejected"), aggregate);
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

        var error = Assert.Throws<SqliteException>(() => SqliteProgressiveReviewGlanceCircuitStore.Setup(Db()));

        Assert.Contains("reject version", error.Message);
        using var current = Open(Db());
        Assert.Equal(before, Schema(current));
        Assert.All(OriginalTables, table => Assert.DoesNotContain(table, Tables(current)));
        Assert.Null(StoreSchemaVersions.Read(current, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName));
    }

    [Fact]
    public void Setup_RecordedVersionTwo_LeavesFileUntouched()
    {
        SqliteProgressiveReviewGlanceCircuitStore.Setup(Db());
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'original'");
        var bytes = File.ReadAllBytes(Db());

        SqliteProgressiveReviewGlanceCircuitStore.Setup(Db());

        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        using var current = Open(Db());
        Assert.Equal(2, StoreSchemaVersions.Read(current, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.StoreName));
    }

    private static void SetupLegacy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        RunNonQuery(connection, """
            CREATE TABLE progressive_review_glance_circuits (
                circuit_identity       TEXT PRIMARY KEY,
                provider               TEXT NOT NULL,
                profile                TEXT NOT NULL,
                model                  TEXT NOT NULL,
                command_fingerprint    TEXT NOT NULL,
                output_format          TEXT NOT NULL,
                parser_contract_version TEXT NOT NULL,
                state                  TEXT NOT NULL CHECK(state IN ('Closed', 'ProbeInFlight', 'Open')),
                probe_lease_id         TEXT,
                probe_expires_at       TEXT,
                opening_cause          TEXT,
                opening_reason         TEXT,
                suppression_count      INTEGER NOT NULL DEFAULT 0,
                updated_at             TEXT NOT NULL
            );
            CREATE TABLE progressive_review_glance_suppressions (
                aggregation_key            TEXT PRIMARY KEY,
                round_key                  TEXT NOT NULL,
                goal_id                    TEXT NOT NULL,
                task_id                    TEXT NOT NULL,
                circuit_identity           TEXT NOT NULL,
                admission_outcome          TEXT NOT NULL,
                opening_cause              TEXT NOT NULL,
                original_reason            TEXT NOT NULL,
                probe_outcome               TEXT NOT NULL,
                avoided_call_count          INTEGER NOT NULL,
                avoided_input_tokens        INTEGER NOT NULL,
                admission_latency_ms        INTEGER NOT NULL,
                changed_files_trigger_count INTEGER NOT NULL,
                elapsed_trigger_count       INTEGER NOT NULL,
                updated_at                  TEXT NOT NULL
            );
            """);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO progressive_review_glance_circuits (
                circuit_identity, provider, profile, model, command_fingerprint, output_format,
                parser_contract_version, state, opening_cause, opening_reason, suppression_count, updated_at
            ) VALUES ($circuit, $provider, $profile, $model, $fingerprint, $format, $parser,
                'Open', 'OutputContract', 'preserved-reason', 7, '1970-01-01T00:00:00.0000000+00:00');
            INSERT INTO progressive_review_glance_suppressions (
                aggregation_key, round_key, goal_id, task_id, circuit_identity, admission_outcome,
                opening_cause, original_reason, probe_outcome, avoided_call_count, avoided_input_tokens,
                admission_latency_ms, changed_files_trigger_count, elapsed_trigger_count, updated_at
            ) VALUES ('aggregate', 'round', 'goal', 'task', $circuit, 'SuppressedOpen', 'OutputContract',
                'preserved-reason', 'Rejected', 3, 120, 45, 2, 1, '1970-01-01T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$circuit", Identity.CircuitIdentity);
        command.Parameters.AddWithValue("$provider", Identity.Provider);
        command.Parameters.AddWithValue("$profile", Identity.Profile);
        command.Parameters.AddWithValue("$model", Identity.Model);
        command.Parameters.AddWithValue("$fingerprint", Identity.CommandFingerprint);
        command.Parameters.AddWithValue("$format", Identity.OutputFormat);
        command.Parameters.AddWithValue("$parser", Identity.ParserContractVersion);
        command.ExecuteNonQuery();
    }

    private static void AssertSecondSetupUnchanged(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string versions;
        using (var connection = Open(path))
            versions = RawRows(connection, "SELECT * FROM store_schema_versions ORDER BY store_name");

        SqliteProgressiveReviewGlanceCircuitStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var current = Open(path);
        Assert.Equal(versions, RawRows(current, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    private static string OriginalRows(SqliteConnection connection) => JsonSerializer.Serialize(
        OriginalTables.Select(table => RawRows(connection, $"SELECT * FROM {table} ORDER BY rowid")).ToArray());

    private static string RawRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }
        return JsonSerializer.Serialize(rows);
    }

    private static string[] Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables.ToArray();
    }

    private static string[] Schema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name";
        using var reader = command.ExecuteReader();
        var schema = new List<string>();
        while (reader.Read())
            schema.Add(reader.GetString(0));
        return schema.ToArray();
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
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
