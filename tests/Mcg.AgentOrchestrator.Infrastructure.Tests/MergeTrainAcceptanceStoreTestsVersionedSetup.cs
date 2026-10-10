using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns a unique temporary directory and disables connection pooling.
public sealed class MergeTrainAcceptanceStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "merge-train-setup-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] OriginalTables =
    [
        "merge_train_ejections", "merge_train_implicated_candidates", "merge_train_landings",
        "merge_train_pair_suppressions", "merge_train_receipts"
    ];
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
            MergeTrainAcceptanceStore.Setup(path);
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            RunNonQuery(connection, scenario == "missing-record"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'merge-train-acceptance'"
                : $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)} WHERE store_name = 'merge-train-acceptance'");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => MergeTrainAcceptanceStore.OpenReadOnly(path));

        Assert.Equal($"Merge train acceptance store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Setup_FreshDatabase_CreatesSixTablesAndVersionOne()
    {
        Assert.False(File.Exists(Db()));

        MergeTrainAcceptanceStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables.Concat(["merge_train_receipt_members", "store_schema_versions"])
                .Order(StringComparer.Ordinal), Tables(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        }
        Assert.Empty(MergeTrainAcceptanceStore.OpenReadOnly(Db()).ReadPassedReceiptsForGoal(Binding('a').GoalId));
        AssertSecondSetupUnchanged(Db());
    }

    [Fact]
    public void Setup_OldShapeDatabase_BackfillsMembersAndPreservesRows()
    {
        var receipts = SetupLegacy(Db());
        string rows;
        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables, Tables(connection));
            rows = OriginalRows(connection);
        }

        MergeTrainAcceptanceStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(rows, OriginalRows(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
            AssertMembers(connection, receipts);
        }
        var bytes = File.ReadAllBytes(Db());
        var readOnly = MergeTrainAcceptanceStore.OpenReadOnly(Db());
        foreach (var goal in receipts.SelectMany(receipt => receipt.Identity.Members).Select(member => member.GoalId).Distinct())
        {
            var expected = receipts.Where(receipt => receipt.Identity.Members.Any(member => member.GoalId == goal))
                .OrderByDescending(receipt => receipt.CompletedAt)
                .ThenByDescending(receipt => receipt.ReceiptId, StringComparer.Ordinal).ToArray();
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(readOnly.ReadPassedReceiptsForGoal(goal)));
        }
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        AssertSecondSetupUnchanged(Db());
    }

    [Fact]
    public void Setup_CurrentVersionWithMemberTableDropped_RebuildsAndKeepsVersion()
    {
        var receipts = SeedReceipts(Db());
        string versions;
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
        {
            RunNonQuery(connection, """
                UPDATE store_schema_versions SET applied_at = 'original';
                DROP TABLE merge_train_receipt_members;
                """);
            Assert.DoesNotContain("merge_train_receipt_members", Tables(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
            versions = RawRows(connection, "SELECT * FROM store_schema_versions ORDER BY store_name");
        }

        MergeTrainAcceptanceStore.Setup(Db());

        using var current = Open(Db());
        AssertMembers(current, receipts);
        Assert.Equal(1, StoreSchemaVersions.Read(current, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        Assert.Equal(versions, RawRows(current, "SELECT * FROM store_schema_versions ORDER BY store_name"));
    }

    [Fact]
    public void Setup_VersionRecordingFails_RollsBackMemberTable()
    {
        SetupLegacy(Db());
        string[] schema;
        string rows;
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
        {
            RunNonQuery(connection, """
                CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL);
                CREATE TRIGGER reject_version BEFORE INSERT ON store_schema_versions BEGIN SELECT RAISE(ABORT, 'reject version'); END;
                """);
            schema = Schema(connection);
            rows = OriginalRows(connection);
            Assert.DoesNotContain("merge_train_receipt_members", Tables(connection));
        }

        var error = Assert.Throws<SqliteException>(() => MergeTrainAcceptanceStore.Setup(Db()));

        Assert.Contains("reject version", error.Message);
        using var current = Open(Db());
        Assert.Equal(schema, Schema(current));
        Assert.DoesNotContain("merge_train_receipt_members", Tables(current));
        Assert.Null(StoreSchemaVersions.Read(current, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        Assert.Equal(rows, OriginalRows(current));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Setup_RecordedVersion_UpgradesOlderAndLeavesNewerUntouched(int version)
    {
        MergeTrainAcceptanceStore.Setup(Db());
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
            RunNonQuery(connection, $"""
                UPDATE store_schema_versions SET version = {version}, applied_at = 'original';
                DROP TABLE merge_train_receipt_members;
                """);
        var bytes = File.ReadAllBytes(Db());

        MergeTrainAcceptanceStore.Setup(Db());

        using var current = Open(Db());
        Assert.Equal(version == 0 ? 1 : 2, StoreSchemaVersions.Read(current, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        if (version == 2)
        {
            Assert.DoesNotContain("merge_train_receipt_members", Tables(current));
            Assert.Equal(bytes, File.ReadAllBytes(Db()));
        }
        else
            Assert.Contains("merge_train_receipt_members", Tables(current));
    }

    [Fact]
    public void OpenReadOnly_WriteAttempt_IsRejectedWithoutChangingFile()
    {
        var writable = new MergeTrainAcceptanceStore(Db());
        writable.SuppressPair("existing", "train");
        var bytes = File.ReadAllBytes(Db());
        var readOnly = MergeTrainAcceptanceStore.OpenReadOnly(Db());

        var error = Assert.Throws<SqliteException>(() => readOnly.SuppressPair("new", "train"));

        Assert.Equal(8, error.SqliteErrorCode); // SQLITE_READONLY
        Assert.Equal("existing", Assert.Single(readOnly.ReadSuppressedPairs()));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
    }

    private MergeTrainReceipt[] SetupLegacy(string path)
    {
        var receipts = SeedReceipts(path);
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        RunNonQuery(connection, """
            INSERT INTO merge_train_landings
                SELECT train_id, receipt_id, 'commit', 'prior', 'finalized', 'original'
                FROM merge_train_receipts ORDER BY train_id LIMIT 1;
            INSERT INTO merge_train_ejections (train_attempt_id, goal_id, reason, conflict_paths_json, detail, recorded_at)
                VALUES ('attempt', 'goal', 'conflict', '["src/Example.cs"]', 'detail', 'original');
            INSERT INTO merge_train_implicated_candidates VALUES ('goal', 'candidate', 'train', 'main', 'original');
            INSERT INTO merge_train_pair_suppressions VALUES ('pair', 'train', 'original');
            DROP TABLE merge_train_receipt_members;
            DROP TABLE store_schema_versions;
            """);
        Assert.Equal(OriginalTables, Tables(connection));
        Assert.Null(StoreSchemaVersions.Read(connection, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        return receipts;
    }

    private MergeTrainReceipt[] SeedReceipts(string path)
    {
        var store = new MergeTrainAcceptanceStore(path);
        return [SavePassedReceipt(store, "first", 'a', 'b'), SavePassedReceipt(store, "second", 'a', 'c')];
    }

    private MergeTrainReceipt SavePassedReceipt(MergeTrainAcceptanceStore store, string id, char goal, char otherGoal)
    {
        var trxPath = Path.Combine(root, id + ".trx");
        File.WriteAllText(trxPath, $$"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="{{id}}" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        var identity = MergeTrainIdentity.Create([Binding(goal), Binding(otherGoal)],
            new string('f', 40), new string('1', 40), "manifest-v1");
        return store.SaveGateReceipt(new MergeTrainReceipt(id, identity, MergeTrainGateOutcome.Passed,
            DateTimeOffset.UnixEpoch, 1, [], 0, [trxPath], ValidForLanding: true));
    }

    private static MergeTrainMemberBinding Binding(char goal) => new(
        new GoalId(new string(goal, 32)), new string(goal, 40), new string(goal, 40),
        ["src/Example.cs"], [], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
        "Clean", "NoConflictsDetected", new string(goal, 40));

    private static void AssertMembers(SqliteConnection connection, IEnumerable<MergeTrainReceipt> receipts)
    {
        var expected = receipts.SelectMany(receipt => receipt.Identity.Members
                .Select(member => new object[] { member.GoalId.Value, receipt.Identity.Value }))
            .OrderBy(row => (string)row[0], StringComparer.Ordinal)
            .ThenBy(row => (string)row[1], StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(JsonSerializer.Serialize(expected),
            RawRows(connection, "SELECT goal_id, train_id FROM merge_train_receipt_members ORDER BY goal_id, train_id"));
    }

    private static void AssertSecondSetupUnchanged(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string versions;
        using (var connection = Open(path))
            versions = RawRows(connection, "SELECT * FROM store_schema_versions ORDER BY store_name");

        MergeTrainAcceptanceStore.Setup(path);

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
