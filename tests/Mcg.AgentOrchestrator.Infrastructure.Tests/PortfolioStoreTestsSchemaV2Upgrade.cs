using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: frozen v1 schema and migration run only in this test's private workspace.
public sealed class PortfolioStoreTestsSchemaV2Upgrade : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();

    [Fact]
    public async Task Version1_UpgradePreservesEpicsDescriptionsAndMemberships_AndListsUnplannedMembers()
    {
        var workspace = CreateRefinedWorkspace(root);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.PortfolioStorePath)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = workspace.PortfolioStorePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Frozen portfolio v1 DDL. No current setup/version helper participates in constructing the fixture.
            command.CommandText = """
                CREATE TABLE projects (id TEXT PRIMARY KEY, title TEXT NOT NULL, parent_project_id TEXT NULL,
                    created_at TEXT NOT NULL, created_by TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
                    FOREIGN KEY(parent_project_id) REFERENCES projects(id) ON DELETE SET NULL);
                CREATE INDEX idx_projects_title ON projects(title COLLATE NOCASE);
                CREATE TABLE epics (id TEXT PRIMARY KEY, title TEXT NOT NULL, project_id TEXT NULL,
                    created_at TEXT NOT NULL, created_by TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
                    description TEXT NULL, FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE SET NULL);
                CREATE INDEX idx_epics_title ON epics(title COLLATE NOCASE);
                CREATE INDEX idx_epics_project ON epics(project_id);
                CREATE TABLE goal_epic_memberships (goal_id TEXT PRIMARY KEY, epic_id TEXT NOT NULL,
                    created_at TEXT NOT NULL, created_by TEXT NOT NULL, FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE);
                CREATE INDEX idx_goal_epic_memberships_epic ON goal_epic_memberships(epic_id);
                CREATE TABLE backlog_epic_memberships (backlog_item_id TEXT PRIMARY KEY, epic_id TEXT NOT NULL,
                    created_at TEXT NOT NULL, created_by TEXT NOT NULL, FOREIGN KEY(epic_id) REFERENCES epics(id) ON DELETE CASCADE);
                CREATE INDEX idx_backlog_epic_memberships_epic ON backlog_epic_memberships(epic_id);
                CREATE TABLE cluster_suggestions (id TEXT PRIMARY KEY, signal TEXT NOT NULL, title TEXT NOT NULL, evidence TEXT NOT NULL,
                    goal_ids_json TEXT NOT NULL, backlog_item_ids_json TEXT NOT NULL, created_at TEXT NOT NULL);
                CREATE INDEX idx_cluster_suggestions_signal ON cluster_suggestions(signal, created_at);
                CREATE TABLE store_schema_versions (store_name TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL) WITHOUT ROWID;
                INSERT INTO store_schema_versions VALUES ('portfolio', 1, '2026-01-01T00:00:00Z');
                INSERT INTO projects VALUES ('project-v1', 'Project', NULL, '2026-01-01T00:00:00Z', 'owner', '2026-01-01T00:00:00Z', 'owner');
                INSERT INTO epics VALUES ('epic-v1', 'Existing epic', 'project-v1', '2026-01-01T00:00:00Z', 'owner', '2026-01-01T00:00:00Z', 'owner', 'Existing purpose');
                INSERT INTO epics VALUES ('other-v1', 'Other epic', NULL, '2026-01-01T00:00:00Z', 'owner', '2026-01-01T00:00:00Z', 'owner', NULL);
                INSERT INTO goal_epic_memberships VALUES ('goal-v1', 'epic-v1', '2026-01-01T00:00:00Z', 'owner');
                INSERT INTO backlog_epic_memberships VALUES ('backlog-v1', 'epic-v1', '2026-01-01T00:00:00Z', 'owner');
                """;
            command.ExecuteNonQuery();
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "portfolio"));
            Assert.Equal(StoreSchemaState.Older, StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.Portfolio));
        }
        var legacyBytes = File.ReadAllBytes(workspace.PortfolioStorePath);
        Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["epic-plan-add", "epic-v1", "--backlog", "unknown"], new AgentOrchestratorKernel(), workspace));
        Assert.Equal(legacyBytes, File.ReadAllBytes(workspace.PortfolioStorePath));
        Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["epic-plan-done", "epic-v1", "1"], new AgentOrchestratorKernel(), workspace));
        Assert.Equal(legacyBytes, File.ReadAllBytes(workspace.PortfolioStorePath));
        var textFile = Path.Combine(root, "legacy-step.txt");
        File.WriteAllText(textFile, "Step");
        Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["epic-plan-add", "epic-v1", "--step-file", textFile, "--at", "2"], new AgentOrchestratorKernel(), workspace));
        Assert.Equal(legacyBytes, File.ReadAllBytes(workspace.PortfolioStorePath));

        var setupPath = Path.Combine(root, "setup-v1.db");
        File.Copy(workspace.PortfolioStorePath, setupPath);
        PortfolioStore.Setup(setupPath);
        Assert.Equal("Existing purpose", (await PortfolioStore.OpenReadOnly(setupPath).ResolveEpicAsync("epic-v1"))!.Description);

        // A valid write upgrades existing v1 data only after validation; blank bar leaves the plan empty.
        File.WriteAllText(textFile, "");
        ExecuteCliAndCapture(["epic-bar", "epic-v1", "--text-file", textFile], new AgentOrchestratorKernel(), workspace);
        var planStore = new EpicPlanStore(workspace.PortfolioStorePath);
        var portfolio = PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath);
        Assert.Equal(2, (await portfolio.ListEpicsAsync()).Count);
        var epic = (await portfolio.ResolveEpicAsync("epic-v1"))!;
        Assert.Equal("Existing epic", epic.Title);
        Assert.Equal("Existing purpose", epic.Description);
        Assert.Equal("project-v1", epic.ProjectId);
        Assert.Equal("owner", epic.CreatedBy);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), epic.CreatedAt);
        Assert.Equal(epic.CreatedAt, epic.UpdatedAt);
        Assert.Equal("owner", epic.UpdatedBy);
        Assert.Null((await portfolio.ResolveEpicAsync("other-v1"))!.Description);
        Assert.Equal("Project", Assert.Single(await portfolio.ListProjectsAsync()).Title);
        var members = await portfolio.ListEpicMembersAsync(epic.Id);
        Assert.Equal(2, members.Count);
        Assert.All(members, member => { Assert.Equal("owner", member.CreatedBy); Assert.Equal(epic.CreatedAt, member.CreatedAt); });
        Assert.Equal(epic.Id, (await portfolio.GetGoalMembershipAsync("goal-v1"))!.EpicId);
        Assert.Equal(epic.Id, (await portfolio.GetBacklogMembershipAsync("backlog-v1"))!.EpicId);
        var plan = await planStore.LoadAsync(epic.Id);
        Assert.Null(plan.Bar);
        Assert.Empty(plan.Items);
        Assert.Empty(plan.Decisions);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = workspace.PortfolioStorePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            Assert.Equal(2, StoreSchemaVersions.Read(connection, "portfolio"));
        }
        var bytes = File.ReadAllBytes(workspace.PortfolioStorePath);
        var output = ExecuteCliAndCapture(["epic-plan", epic.Id], new AgentOrchestratorKernel(), workspace);
        Assert.Contains($"Not in plan:{Environment.NewLine}  Goal: goal-v1{Environment.NewLine}  BacklogItem: backlog-v1", output);
        Assert.Contains("Purpose: Existing purpose", output);
        Assert.Contains("Next step: no plan", output);
        Assert.DoesNotContain("Stalled", output);
        Assert.Equal(bytes, File.ReadAllBytes(workspace.PortfolioStorePath));
        Assert.False(File.Exists(workspace.BacklogStorePath));
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
