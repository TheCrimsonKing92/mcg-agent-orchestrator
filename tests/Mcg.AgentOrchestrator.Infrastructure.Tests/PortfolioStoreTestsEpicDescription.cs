using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every test owns its database directory and uses non-pooled connections.
public sealed class PortfolioStoreTestsEpicDescription : IDisposable
{
    private readonly string root = CreateTempDirectory();
    private string DbPath => Path.Combine(root, "portfolio.db");

    [Fact]
    public async Task Open_SevenColumnDatabase_AddsNullableColumnIdempotently()
    {
        SeedLegacyEpic();
        using (var conn = OpenRawConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('epics')";
            Assert.Equal(7L, cmd.ExecuteScalar());
        }

        var epic = Assert.Single(await new PortfolioStore(DbPath).ListEpicsAsync());
        Assert.Equal("legacy-epic", epic.Id);
        Assert.Equal("Legacy title", epic.Title);
        Assert.Null(epic.Description);
        Assert.Equal(DateTimeOffset.Parse("2020-01-01T00:00:00+00:00"), epic.UpdatedAt);
        Assert.Equal(epic, Assert.Single(await new PortfolioStore(DbPath).ListEpicsAsync()));
        using (var conn = OpenRawConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('epics') WHERE name = 'description' AND type = 'TEXT' AND [notnull] = 0";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task Add_WithDescription_RoundTripsThroughEveryEpicRead()
    {
        var store = new PortfolioStore(DbPath);
        var epic = await store.AddEpicAsync("  Remote execution  ", description: "  Scope\nDone condition  ");
        Assert.Equal("Remote execution", epic.Title);
        Assert.Equal("Scope\nDone condition", epic.Description);
        Assert.Equal(epic, Assert.Single(await new PortfolioStore(DbPath).ListEpicsAsync()));
        Assert.Equal(epic, await store.ResolveEpicAsync(epic.Id[..8]));
        Assert.Equal(epic, Assert.Single(await store.BuildEpicRollupsAsync([])).Epic);
    }

    [Fact]
    public async Task Update_BothFields_PersistsValuesAndAuditWithoutChangingMembership()
    {
        SeedLegacyEpic();
        var store = new PortfolioStore(DbPath);
        var original = Assert.Single(await store.ListEpicsAsync());
        var project = await store.AddProjectAsync("Control plane");
        await store.AssignEpicToProjectAsync(original.Id, project.Id);
        await store.AssignGoalToEpicAsync("goal-member", original.Id);
        await store.AssignBacklogItemToEpicAsync("backlog-member", original.Id);
        // Project assignment also writes the audit timestamp; restore the fixed baseline
        // so this assertion specifically detects UpdateEpicAsync writing updated_at.
        using (var conn = OpenRawConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE epics SET updated_at = $timestamp WHERE id = $id";
            cmd.Parameters.AddWithValue("$timestamp", original.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$id", original.Id);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var beforeRollup = Assert.Single(await store.BuildEpicRollupsAsync([]));
        Assert.Equal(original.UpdatedAt, beforeRollup.Epic.UpdatedAt);

        var updated = await store.UpdateEpicAsync(original.Id, "  New title  ", "  New description  ", actor: "author");
        Assert.Equal("New title", updated.Title);
        Assert.Equal("New description", updated.Description);
        Assert.NotEqual(original.UpdatedAt, updated.UpdatedAt);
        Assert.Equal("author", updated.UpdatedBy);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.Equal(original.CreatedBy, updated.CreatedBy);
        Assert.Equal(project.Id, updated.ProjectId);
        Assert.Equal(updated, Assert.Single(await new PortfolioStore(DbPath).ListEpicsAsync()));
        Assert.Equal(beforeRollup with { Epic = updated }, Assert.Single(await store.BuildEpicRollupsAsync([])));
        Assert.Equal(original.Id, (await store.GetGoalMembershipAsync("goal-member"))!.EpicId);
        Assert.Equal(original.Id, (await store.GetBacklogMembershipAsync("backlog-member"))!.EpicId);
    }

    [Fact]
    public async Task Rename_DuplicateTitleInDifferentCase_RollsBackBothEpics()
    {
        var store = new PortfolioStore(DbPath);
        var first = await store.AddEpicAsync("First epic", description: "First scope");
        var second = await store.AddEpicAsync("Second epic", description: "Second scope");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateEpicAsync(second.Id, "FIRST EPIC", "Should roll back", actor: "author"));
        Assert.Contains(first.Id, error.Message);
        var epics = await new PortfolioStore(DbPath).ListEpicsAsync();
        Assert.Equal(first, Assert.Single(epics, epic => epic.Id == first.Id));
        Assert.Equal(second, Assert.Single(epics, epic => epic.Id == second.Id));
    }

    [Fact]
    public async Task Update_OmittedFieldsPreservesValues_ExplicitBlankClearsDescription()
    {
        var store = new PortfolioStore(DbPath);
        var epic = await store.AddEpicAsync("Title", description: "Scope");
        var renamed = await store.UpdateEpicAsync(epic.Id, title: "TITLE");
        Assert.Equal("TITLE", renamed.Title);
        Assert.Equal("Scope", renamed.Description);
        var described = await store.UpdateEpicAsync(epic.Id, description: "Replacement");
        Assert.Equal("TITLE", described.Title);
        Assert.Equal("Replacement", described.Description);
        await store.UpdateEpicAsync(epic.Id, description: " \r\n ");
        Assert.Null((await store.ResolveEpicAsync(epic.Id))!.Description);
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateEpicAsync(epic.Id, title: " "));
        Assert.Equal("TITLE", (await store.ResolveEpicAsync(epic.Id))!.Title);
    }

    private void SeedLegacyEpic()
    {
        using var conn = OpenRawConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE epics (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, project_id TEXT NULL,
                created_at TEXT NOT NULL, created_by TEXT NOT NULL,
                updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
                FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL
            );
            INSERT INTO epics VALUES ('legacy-epic', 'Legacy title', NULL,
                '2020-01-01T00:00:00+00:00', 'seed', '2020-01-01T00:00:00+00:00', 'seed');
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenRawConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
