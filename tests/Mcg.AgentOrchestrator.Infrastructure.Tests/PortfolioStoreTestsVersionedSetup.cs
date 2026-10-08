using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every test instance owns its temporary databases, including both lock connections.
public sealed class PortfolioStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "portfolio-setup-" + Guid.NewGuid().ToString("N"));
    private string Db(string name = "portfolio.db") => Path.Combine(root, name);

    [Fact]
    public void Setup_FreshDatabase_MatchesFrozenLegacySchemaWithOnlyVersionTableAdded()
    {
        SetupLegacy(Db("legacy.db"));
        PortfolioStore.Setup(Db());
        using var legacy = Open(Db("legacy.db"));
        using var current = Open(Db());
        var oldSchema = Schema(legacy);
        var newSchema = Schema(current);

        var added = Assert.Single(newSchema.Except(oldSchema));
        Assert.Equal("table", added.Type);
        Assert.Equal(StoreSchemaVersions.TableName, added.Name);
        Assert.Equal(oldSchema, newSchema.Where(row => row.Name != added.Name).ToArray());
        foreach (var table in oldSchema.Where(row => row.Type == "table"))
            Assert.Equal(Columns(legacy, table.Name), Columns(current, table.Name));
        Assert.Equal(["store_name", "version", "applied_at"], Columns(current, added.Name).Select(column => column.Name).ToArray());
        Assert.Equal(StoreSchemaRegistry.Portfolio.CurrentVersion, StoreSchemaVersions.Read(current, "portfolio"));
    }

    [Fact]
    public async Task OpenReadOnly_SetUpDatabase_ReturnsSameRowsWithoutChangingFile()
    {
        var writable = new PortfolioStore(Db());
        var project = await writable.AddProjectAsync("Project");
        var epic = await writable.AddEpicAsync("Epic", project.Id, description: "Description");
        await writable.AssignGoalToEpicAsync("goal", epic.Id);
        await writable.AssignBacklogItemToEpicAsync("backlog", epic.Id);
        var projects = await writable.ListProjectsAsync();
        var epics = await writable.ListEpicsAsync();
        var members = await writable.ListEpicMembersAsync(epic.Id);
        var goalMembership = await writable.GetGoalMembershipAsync("goal");
        var backlogMembership = await writable.GetBacklogMembershipAsync("backlog");
        Assert.Equal(2, members.Count);
        var bytes = File.ReadAllBytes(Db());
        var modified = File.GetLastWriteTimeUtc(Db());

        var readOnly = PortfolioStore.OpenReadOnly(Db());
        Assert.Equal(projects.ToArray(), (await readOnly.ListProjectsAsync()).ToArray());
        Assert.Equal(epics.ToArray(), (await readOnly.ListEpicsAsync()).ToArray());
        Assert.Equal(members.ToArray(), (await readOnly.ListEpicMembersAsync(epic.Id)).ToArray());
        Assert.Equal(goalMembership, await readOnly.GetGoalMembershipAsync("goal"));
        Assert.Equal(backlogMembership, await readOnly.GetBacklogMembershipAsync("backlog"));
        Assert.Equal(bytes, File.ReadAllBytes(Db()));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Db()));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("unversioned")]
    [InlineData("missing-record")]
    [InlineData("older")]
    [InlineData("newer")]
    public void OpenReadOnly_IncompatibleDatabase_RequiresSetupWithoutWriting(string scenario)
    {
        var path = scenario == "missing-file" ? Db("absent/portfolio.db") : Db();
        if (scenario == "unversioned")
            SetupLegacy(path);
        else if (scenario != "missing-file")
        {
            PortfolioStore.Setup(path);
            using var conn = Open(path);
            if (scenario == "missing-record")
                RunNonQuery(conn, "DELETE FROM store_schema_versions WHERE store_name = 'portfolio'");
            else
                RunNonQuery(conn, $"UPDATE store_schema_versions SET version = {StoreSchemaRegistry.Portfolio.CurrentVersion + (scenario == "older" ? -1 : 1)}");
        }
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var modified = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;

        var error = Assert.Throws<InvalidOperationException>(() => PortfolioStore.OpenReadOnly(path));
        Assert.Contains("run setup", error.Message);
        if (scenario == "missing-file")
        {
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        }
        else
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task OpenReadOnly_WhileSecondConnectionHoldsWriteLock_ReadsCommittedRows()
    {
        var writable = new PortfolioStore(Db());
        var epic = await writable.AddEpicAsync("Committed epic");
        using var writer = Open(Db());
        RunNonQuery(writer, "BEGIN IMMEDIATE");
        try
        {
            RunNonQuery(writer, """
                INSERT INTO epics (id, title, created_at, created_by, updated_at, updated_by)
                VALUES ('uncommitted', 'Hidden epic', '2026-01-01T00:00:00Z', 'test', '2026-01-01T00:00:00Z', 'test')
                """);
            using (var cmd = writer.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM epics";
                Assert.Equal(2L, cmd.ExecuteScalar());
            }

            var readOnly = PortfolioStore.OpenReadOnly(Db());
            Assert.Equal(epic, Assert.Single(await readOnly.ListEpicsAsync()));
        }
        finally
        {
            RunNonQuery(writer, "ROLLBACK");
        }
    }

    [Fact]
    public async Task Setup_LegacyDatabaseWithoutDescription_PreservesRowsAndAddsColumnAndVersion()
    {
        SetupLegacy(Db());
        using (var legacy = Open(Db()))
        {
            RunNonQuery(legacy, "ALTER TABLE epics DROP COLUMN description");
            RunNonQuery(legacy, """
                INSERT INTO epics (id, title, created_at, created_by, updated_at, updated_by)
                VALUES ('legacy', 'Existing epic', '2026-01-01T00:00:00Z', 'test', '2026-01-01T00:00:00Z', 'test')
                """);
        }

        PortfolioStore.Setup(Db());
        var epic = Assert.Single(await PortfolioStore.OpenReadOnly(Db()).ListEpicsAsync());
        Assert.Equal("legacy", epic.Id);
        Assert.Equal("Existing epic", epic.Title);
        Assert.Null(epic.Description);
    }

    [Fact]
    public async Task OpenReadOnly_WriteAttempt_IsRejectedBySqlite()
    {
        PortfolioStore.Setup(Db());
        var error = await Assert.ThrowsAsync<SqliteException>(() => PortfolioStore.OpenReadOnly(Db()).AddEpicAsync("Forbidden"));
        Assert.Equal(8, error.SqliteErrorCode);
        Assert.Empty(await PortfolioStore.OpenReadOnly(Db()).ListEpicsAsync());
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record SchemaRow(string Type, string Name, string Table, string? Sql);
    private sealed record ColumnRow(long Id, string Name, string Type, long NotNull, string? DefaultValue, long PrimaryKey);

    private static SchemaRow[] Schema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name";
        using var reader = cmd.ExecuteReader();
        var rows = new List<SchemaRow>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows.ToArray();
    }

    private static ColumnRow[] Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var reader = cmd.ExecuteReader();
        var rows = new List<ColumnRow>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5)));
        return rows.ToArray();
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Frozen pre-change constructor schema from HEAD 9315b47970ff2a65da6dd0e11acbeb0cfdf9b056.
    // DDL is copied verbatim; only method parameters and connection construction are adapted.
    private static void SetupLegacy(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, "PRAGMA foreign_keys=ON");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS projects (
                id                TEXT PRIMARY KEY,
                title             TEXT NOT NULL,
                parent_project_id TEXT NULL,
                created_at        TEXT NOT NULL,
                created_by        TEXT NOT NULL,
                updated_at        TEXT NOT NULL,
                updated_by        TEXT NOT NULL,
                FOREIGN KEY(parent_project_id) REFERENCES projects(id) ON DELETE SET NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_projects_title ON projects(title COLLATE NOCASE)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS epics (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                project_id TEXT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL,
                description TEXT NULL,
                FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_epics_title ON epics(title COLLATE NOCASE)");
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_epics_project ON epics(project_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS goal_epic_memberships (
                goal_id    TEXT PRIMARY KEY,
                epic_id    TEXT NOT NULL,
                created_at TEXT NOT NULL,
                created_by TEXT NOT NULL,
                FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_goal_epic_memberships_epic ON goal_epic_memberships(epic_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS backlog_epic_memberships (
                backlog_item_id TEXT PRIMARY KEY,
                epic_id         TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                created_by      TEXT NOT NULL,
                FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_backlog_epic_memberships_epic ON backlog_epic_memberships(epic_id)");
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS cluster_suggestions (
                id                    TEXT PRIMARY KEY,
                signal                TEXT NOT NULL,
                title                 TEXT NOT NULL,
                evidence              TEXT NOT NULL,
                goal_ids_json         TEXT NOT NULL,
                backlog_item_ids_json TEXT NOT NULL,
                created_at            TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS idx_cluster_suggestions_signal ON cluster_suggestions(signal, created_at)");
        RunNonQuery(conn, "BEGIN IMMEDIATE");
        try
        {
            var hasDescription = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(epics)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    hasDescription |= reader.GetString(1).Equals("description", StringComparison.OrdinalIgnoreCase);
            }
            if (!hasDescription)
                RunNonQuery(conn, "ALTER TABLE epics ADD COLUMN description TEXT NULL");
            RunNonQuery(conn, "COMMIT");
        }
        catch
        {
            RunNonQuery(conn, "ROLLBACK");
            throw;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
