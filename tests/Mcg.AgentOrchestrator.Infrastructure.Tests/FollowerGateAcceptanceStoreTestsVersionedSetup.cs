using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns a private workspace and every connection disables pooling.
public sealed class FollowerGateAcceptanceStoreVersionedSetupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_LegacyOrOlder_PreservesRowsAndIsIdempotent(bool older)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var expected = CreateLegacyDatabase(path);
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWrite))
        {
            Assert.Null(StoreSchemaVersions.Read(connection, StoreSchemaRegistry.FollowerGateAcceptance.StoreName));
            if (older)
            {
                StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.FollowerGateAcceptance);
                RunSql(connection, "UPDATE store_schema_versions SET version = 0");
            }
        }

        FollowerGateAcceptanceStoreSetup.Setup(path);

        AssertReaderRows(expected, FollowerGateAcceptanceStore.OpenReadOnly(path));
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.FollowerGateAcceptance.StoreName));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM follower_gate_receipts";
            Assert.Equal((long)expected.Length, command.ExecuteScalar());
            RunSql(connection, "UPDATE store_schema_versions SET applied_at = 'original'");
        }
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        FollowerGateAcceptanceStoreSetup.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        AssertReaderRows(expected, FollowerGateAcceptanceStore.OpenReadOnly(path));
    }

    [Fact]
    public void Setup_NewerVersion_LeavesBytesSchemaAndJournalModeUntouched()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        CreateLegacyDatabase(path);
        FollowerGateAcceptanceStoreSetup.Setup(path);
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWrite))
            RunSql(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'future'");
        var journalMode = JournalMode(path);
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        FollowerGateAcceptanceStoreSetup.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        Assert.Equal(journalMode, JournalMode(path));
        using var readBack = OpenConnection(path);
        Assert.Equal(2, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.FollowerGateAcceptance.StoreName));
    }

    [Theory]
    [InlineData("missing", StoreSchemaState.Missing)]
    [InlineData("unversioned", StoreSchemaState.Missing)]
    [InlineData("missing-record", StoreSchemaState.Missing)]
    [InlineData("older", StoreSchemaState.Older)]
    [InlineData("newer", StoreSchemaState.Newer)]
    public void OpenReadOnly_NonCurrentSchema_RefusesWithoutMutation(string scenario, StoreSchemaState state)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.RootDirectory, "absent-directory", "follower-gate-acceptance.db");
        if (scenario == "unversioned")
            CreateLegacyDatabase(path);
        else if (scenario != "missing")
        {
            FollowerGateAcceptanceStoreSetup.Setup(path);
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

        var error = Assert.Throws<InvalidOperationException>(() => FollowerGateAcceptanceStore.OpenReadOnly(path));

        Assert.Equal($"Follower gate acceptance store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
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
    public void OpenReadOnly_CurrentSchema_ReturnsRowsAndRejectsAllWrites()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var writer = new FollowerGateAcceptanceStore(path);
        var expected = ExpectedReceipts();
        foreach (var receipt in expected.Reverse())
            writer.SaveGateReceipt(receipt);
        var bytes = File.ReadAllBytes(path);
        var schema = SchemaSnapshot(path);

        var reader = FollowerGateAcceptanceStore.OpenReadOnly(path);
        AssertReaderRows(expected, reader);
        Assert.Null(reader.TryReadReceipt("absent-identity"));
        Assert.Empty(reader.ReadReceiptsForFollower(new("absent-follower")));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));

        Assert.Throws<InvalidOperationException>(() => reader.SaveGateReceipt(expected[0]));
        var fresh = Receipt("forbidden", "follower", DateTimeOffset.UnixEpoch);
        Assert.Throws<InvalidOperationException>(() => reader.SaveGateReceipt(fresh));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(schema, SchemaSnapshot(path));
        AssertReaderRows(expected, reader);

        // A missing database makes any attempted read fail, proving the writer guard precedes I/O.
        File.Delete(path);
        Assert.Throws<InvalidOperationException>(() => reader.SaveGateReceipt(expected[0]));
        Assert.Throws<InvalidOperationException>(() => reader.SaveGateReceipt(fresh));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Setup_AndReadOnlyReads_PreserveLegacyJournalMode()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        var expected = CreateLegacyDatabase(path);
        var journalMode = JournalMode(path);
        for (var run = 0; run < 2; run++)
        {
            FollowerGateAcceptanceStoreSetup.Setup(path);
            Assert.Equal(journalMode, JournalMode(path));
            AssertReaderRows(expected, FollowerGateAcceptanceStore.OpenReadOnly(path));
            Assert.Equal(journalMode, JournalMode(path));
        }
    }

    [Fact]
    public void Setup_VersionFailure_RollsBackTableAndIndex()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = DatabasePath(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate))
            RunSql(connection, "CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER)");

        Assert.Throws<SqliteException>(() => FollowerGateAcceptanceStoreSetup.Setup(path));

        using var readBack = OpenConnection(path);
        using var command = readBack.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE name IN (
                'follower_gate_receipts', 'follower_gate_receipts_follower')
            """;
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Null(StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.FollowerGateAcceptance.StoreName));
    }

    private static string DatabasePath(ConductorVerbStartTests.Fixture fixture) =>
        Path.Combine(fixture.Workspace.OrchestratorDirectory, "follower-gate-acceptance.db");

    private static FollowerGateRunReceipt[] ExpectedReceipts() =>
    [
        Receipt("z-new", "follower", DateTimeOffset.UnixEpoch.AddDays(1)) with
        { Outcome = FollowerGateRunOutcome.Invalidated, InvalidReason = FollowerGateInvalidReason.LeaderTreeDiffers },
        Receipt("a-new", "follower", DateTimeOffset.UnixEpoch.AddDays(1)) with
        { Outcome = FollowerGateRunOutcome.InfrastructureFailure, GateExitCode = null },
        Receipt("old", "follower", DateTimeOffset.UnixEpoch),
        Receipt("other", "other-follower", DateTimeOffset.UnixEpoch.AddDays(2))
    ];

    private static FollowerGateRunReceipt Receipt(string id, string follower, DateTimeOffset time)
    {
        var binding = new FollowerGateReceipt(new("leader"), new string('a', 40), new string('b', 40),
            new string('c', 40), new(follower), new string('d', 40), new string('e', 40), id);
        return new(id, FollowerGateIdentity.Create(binding), binding, FollowerGateRunOutcome.Failed,
            time, ["check-one", "check-two"], 1, ["first.trx", "second.trx"], null);
    }

    private static FollowerGateRunReceipt[] CreateLegacyDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenConnection(path, SqliteOpenMode.ReadWriteCreate);
        // Frozen pre-versioning schema; do not use the production setup to create this fixture.
        RunSql(connection, """
            CREATE TABLE follower_gate_receipts(
                identity_value TEXT PRIMARY KEY,
                receipt_id TEXT NOT NULL UNIQUE,
                leader_goal_id TEXT NOT NULL,
                follower_goal_id TEXT NOT NULL,
                payload_json TEXT NOT NULL);
            CREATE INDEX follower_gate_receipts_follower ON follower_gate_receipts(follower_goal_id);
            """);
        var expected = ExpectedReceipts();
        foreach (var receipt in expected.Reverse())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO follower_gate_receipts
                    (identity_value, receipt_id, leader_goal_id, follower_goal_id, payload_json)
                VALUES ($identity, $receipt, $leader, $follower, $payload)
                """;
            command.Parameters.AddWithValue("$identity", receipt.IdentityValue);
            command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
            command.Parameters.AddWithValue("$leader", receipt.Binding.LeaderGoalId.Value);
            command.Parameters.AddWithValue("$follower", receipt.Binding.FollowerGoalId.Value);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        return expected;
    }

    private static void AssertReaderRows(FollowerGateRunReceipt[] expected, FollowerGateAcceptanceStore reader)
    {
        foreach (var group in expected.GroupBy(receipt => receipt.Binding.FollowerGoalId))
        {
            var rows = reader.ReadReceiptsForFollower(group.Key);
            var receipts = group.ToArray();
            Assert.Equal(receipts.Length, rows.Count);
            for (var index = 0; index < receipts.Length; index++)
                AssertReceiptEqual(receipts[index], rows[index]);
        }
        foreach (var receipt in expected)
            AssertReceiptEqual(receipt, Assert.IsType<FollowerGateRunReceipt>(reader.TryReadReceipt(receipt.IdentityValue)));
    }

    private static void AssertReceiptEqual(FollowerGateRunReceipt expected, FollowerGateRunReceipt actual)
    {
        Assert.Equal(expected.ReceiptId, actual.ReceiptId);
        Assert.Equal(expected.IdentityValue, actual.IdentityValue);
        Assert.Equal(expected.Binding, actual.Binding);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal<string>(expected.FailedChecks, actual.FailedChecks);
        Assert.Equal(expected.GateExitCode, actual.GateExitCode);
        Assert.Equal<string>(expected.GateTestResultPaths, actual.GateTestResultPaths);
        Assert.Equal(expected.InvalidReason, actual.InvalidReason);
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
