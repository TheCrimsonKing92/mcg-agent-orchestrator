// Parallel-safe: each test owns a unique collaboration-items-setup-<guid> temporary directory and disables connection pooling.
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class CollaborationItemStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "collaboration-items-setup-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] OriginalTables =
    [
        "collaboration_items", "collaboration_item_actions", "collaboration_decision_audit",
        "collaboration_decision_requests", "collaboration_decision_request_actions",
        "collaboration_decision_receipts", "collaboration_notification_deliveries", "collaboration_effect_receipts"
    ];
    private static readonly (string Table, string Column)[] AddedColumns =
    [
        ("collaboration_items", "answer_history_json"),
        ("collaboration_decision_receipts", "reversibility"),
        ("collaboration_decision_receipts", "precedent_ref"),
        ("collaboration_effect_receipts", "outcome")
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
            CollaborationItemStore.Setup(path);
            using var connection = Open(path, SqliteOpenMode.ReadWrite);
            RunNonQuery(connection, scenario == "missing-record"
                ? "DELETE FROM store_schema_versions WHERE store_name = 'collaboration-items'"
                : $"UPDATE store_schema_versions SET version = {(scenario == "older" ? 0 : 2)} WHERE store_name = 'collaboration-items'");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;

        var error = Assert.Throws<InvalidOperationException>(() => CollaborationItemStore.OpenReadOnly(path));

        Assert.Equal($"Collaboration items store '{path}' schema is {state} (expected version 1); run setup.", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task OpenReadOnly_WriteAttempt_IsRejectedWithoutChangingFile()
    {
        _ = new CollaborationItemStore(Db());
        var bytes = File.ReadAllBytes(Db());

        var readOnly = CollaborationItemStore.OpenReadOnly(Db());
        Assert.NotNull(readOnly);
        await Assert.ThrowsAsync<SqliteException>(() => readOnly.RaiseAsync(
            CollaborationItemType.Decision, "goal", "subject", "body"));

        Assert.Equal(bytes, File.ReadAllBytes(Db()));
    }

    [Fact]
    public void Setup_FreshDatabase_CreatesAllTablesAddedColumnsAndVersionOne()
    {
        Assert.False(File.Exists(Db()));

        CollaborationItemStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables.Concat(["store_schema_versions"]).Order(StringComparer.Ordinal), Tables(connection));
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.CollaborationItems.StoreName));
            AssertAddedColumns(connection, present: true);
        }
        AssertSecondSetupUnchanged(Db());
    }

    [Fact]
    public async Task Setup_OldShapeDatabase_AddsColumnsRecordsVersionAndPreservesItem()
    {
        SetupLegacy(Db());
        using (var connection = Open(Db()))
        {
            Assert.Equal(OriginalTables.Order(StringComparer.Ordinal), Tables(connection));
            Assert.Null(StoreSchemaVersions.Read(connection, StoreSchemaRegistry.CollaborationItems.StoreName));
            AssertAddedColumns(connection, present: false);
        }

        CollaborationItemStore.Setup(Db());

        using (var connection = Open(Db()))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.CollaborationItems.StoreName));
            AssertAddedColumns(connection, present: true);
        }
        AssertSecondSetupUnchanged(Db());
        var store = new CollaborationItemStore(Db());
        var item = Assert.Single(await store.ListForGoalIdsAsync(["goal-legacy"]));
        Assert.Equal("legacy-item", item.Id);
        Assert.Equal("legacy subject", item.Subject);
        Assert.Equal("legacy body", item.Body);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_VersionRecordingFails_RollsBackTablesAndColumnAdditions(bool legacy)
    {
        if (legacy)
            SetupLegacy(Db());
        Directory.CreateDirectory(root);
        using (var connection = Open(Db(), SqliteOpenMode.ReadWriteCreate))
            RunNonQuery(connection, """
                CREATE TABLE store_schema_versions (store_name TEXT PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL);
                CREATE TRIGGER reject_version BEFORE INSERT ON store_schema_versions BEGIN SELECT RAISE(ABORT, 'reject version'); END;
                """);
        string[] before;
        using (var connection = Open(Db()))
            before = Schema(connection);

        var error = Assert.Throws<SqliteException>(() => CollaborationItemStore.Setup(Db()));

        Assert.Contains("reject version", error.Message);
        using var current = Open(Db());
        Assert.Equal(before, Schema(current));
        Assert.Null(StoreSchemaVersions.Read(current, StoreSchemaRegistry.CollaborationItems.StoreName));
        if (legacy)
            AssertAddedColumns(current, present: false);
        else
            Assert.All(OriginalTables, table => Assert.DoesNotContain(table, Tables(current)));
    }

    [Fact]
    public void Setup_RecordedVersionTwo_LeavesFileUntouched()
    {
        CollaborationItemStore.Setup(Db());
        using (var connection = Open(Db(), SqliteOpenMode.ReadWrite))
            RunNonQuery(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'original'");
        var bytes = File.ReadAllBytes(Db());
        string versions;
        using (var connection = Open(Db()))
            versions = VersionRows(connection);

        CollaborationItemStore.Setup(Db());

        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        using var current = Open(Db());
        Assert.Equal(2, StoreSchemaVersions.Read(current, StoreSchemaRegistry.CollaborationItems.StoreName));
        Assert.Equal(versions, VersionRows(current));
    }

    private static void SetupLegacy(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        // Independent first-shape fixture: none of the later ALTER TABLE columns or version metadata.
        RunNonQuery(connection, """
            CREATE TABLE collaboration_items (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                goal_id TEXT,
                status TEXT NOT NULL,
                subject TEXT NOT NULL,
                body TEXT NOT NULL,
                correlation_key TEXT,
                raised_at TEXT NOT NULL,
                resolved_at TEXT,
                resolution TEXT
            );
            CREATE INDEX idx_collaboration_items_correlation ON collaboration_items (correlation_key);
            CREATE INDEX idx_collaboration_items_goal_id ON collaboration_items (goal_id);
            CREATE INDEX idx_collaboration_items_status_raised ON collaboration_items (status, raised_at);
            CREATE TABLE collaboration_item_actions (
                correlation_key TEXT NOT NULL,
                action_index INTEGER NOT NULL,
                label TEXT NOT NULL,
                command TEXT NOT NULL,
                requires_confirmation INTEGER NOT NULL,
                requires_input INTEGER NOT NULL,
                expected_goal_state_version INTEGER,
                expires_at TEXT NOT NULL,
                consumed_at TEXT,
                rendered_content_hash TEXT,
                PRIMARY KEY (correlation_key, action_index)
            );
            CREATE INDEX idx_collaboration_item_actions_expiry ON collaboration_item_actions (expires_at);
            CREATE TABLE collaboration_decision_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                correlation_key TEXT NOT NULL,
                action_index INTEGER,
                actor_id TEXT NOT NULL,
                interaction_id TEXT NOT NULL UNIQUE,
                outcome TEXT NOT NULL,
                command TEXT,
                rejection_reason TEXT,
                expected_goal_state_version INTEGER,
                actual_goal_state_version INTEGER,
                rendered_content_hash TEXT,
                decided_at TEXT NOT NULL
            );
            CREATE INDEX idx_collaboration_decision_audit_correlation ON collaboration_decision_audit (correlation_key);
            CREATE TABLE collaboration_decision_requests (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                goal_id TEXT,
                subject TEXT NOT NULL,
                rendered_text TEXT NOT NULL,
                template_version TEXT NOT NULL,
                evidence_manifest_json TEXT NOT NULL,
                evidence_manifest_hash TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                default_disposition TEXT NOT NULL,
                blocking_impact_json TEXT NOT NULL,
                reuse_scopes_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX idx_collaboration_decision_requests_goal ON collaboration_decision_requests (goal_id);
            CREATE TABLE collaboration_decision_request_actions (
                request_id TEXT NOT NULL,
                action_ref TEXT NOT NULL,
                label TEXT NOT NULL,
                kind TEXT NOT NULL,
                required_tier TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                expected_goal_state_version INTEGER,
                PRIMARY KEY (request_id, action_ref)
            );
            CREATE TABLE collaboration_decision_receipts (
                id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL UNIQUE,
                rendered_text TEXT NOT NULL,
                template_version TEXT NOT NULL,
                evidence_manifest_json TEXT NOT NULL,
                evidence_manifest_hash TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                channel TEXT NOT NULL,
                authentication_assurance TEXT NOT NULL,
                expected_goal_state_version INTEGER,
                action_ref TEXT NOT NULL,
                response_value TEXT NOT NULL,
                selected_reuse_scope TEXT NOT NULL,
                permanent_policy_proposed INTEGER NOT NULL,
                recorded_at TEXT NOT NULL
            );
            CREATE TABLE collaboration_notification_deliveries (
                id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL,
                channel TEXT NOT NULL,
                target TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                delivered_at TEXT NOT NULL
            );
            CREATE TABLE collaboration_effect_receipts (
                id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL,
                decision_receipt_id TEXT NOT NULL,
                action_ref TEXT NOT NULL,
                status TEXT NOT NULL,
                expected_goal_state_version INTEGER,
                actual_goal_state_version INTEGER,
                result TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                UNIQUE (request_id, decision_receipt_id, action_ref)
            );
            INSERT INTO collaboration_items (id, type, goal_id, status, subject, body, raised_at)
            VALUES ('legacy-item', 'Decision', 'goal-legacy', 'Raised', 'legacy subject', 'legacy body',
                '1970-01-01T00:00:00.0000000+00:00');
            """);
    }

    private static void AssertAddedColumns(SqliteConnection connection, bool present)
    {
        foreach (var (table, column) in AddedColumns)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
                columns.Add(reader.GetString(1));
            if (present)
                Assert.Contains(column, columns);
            else
                Assert.DoesNotContain(column, columns);
        }
    }

    private static void AssertSecondSetupUnchanged(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string versions;
        using (var connection = Open(path))
            versions = VersionRows(connection);

        CollaborationItemStore.Setup(path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        using var current = Open(path);
        Assert.Equal(versions, VersionRows(current));
    }

    private static string VersionRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM store_schema_versions ORDER BY store_name";
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

    private static string[] Tables(SqliteConnection connection) => QueryStrings(connection,
        "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");

    private static string[] Schema(SqliteConnection connection) => QueryStrings(connection,
        "SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name");

    private static string[] QueryStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
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
