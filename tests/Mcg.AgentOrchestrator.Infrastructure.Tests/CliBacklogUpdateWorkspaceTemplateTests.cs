using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each workspace and its unpooled SQLite connections belong to this test.
public sealed class CliBacklogUpdateWorkspaceTemplateTests : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task CopyTo_RefinedWorkspaceBaseline_MatchesStoreAndHandlerOutcome()
    {
        var baselineRoot = CreateTempDirectory();
        var copiedRoot = CreateTempDirectory();
        try
        {
            var baseline = CreateRefinedWorkspace(baselineRoot);
            var baselineStore = new BacklogStore(baseline.BacklogStorePath);
            CliBacklogUpdateWorkspaceTemplate.CopyTo(copiedRoot);
            var copied = OrchestratorWorkspace.ForDirectory(copiedRoot);

            Assert.Equal(Path.GetRelativePath(baselineRoot, baseline.BacklogStorePath),
                Path.GetRelativePath(copiedRoot, copied.BacklogStorePath));
            var expectedSchema = ReadSchema(baseline.BacklogStorePath);
            Assert.NotEmpty(expectedSchema);
            Assert.Equal(expectedSchema, ReadSchema(copied.BacklogStorePath));
            Assert.Equal(ReadJournalMode(baseline.BacklogStorePath), ReadJournalMode(copied.BacklogStorePath));
            Assert.Empty(await baselineStore.ListAsync(includeAll: true));
            // Read-only opening verifies the copied schema before a constructor could repair it.
            Assert.Empty(await BacklogStore.OpenReadOnly(copied.BacklogStorePath).ListAsync(includeAll: true));
            Assert.False(File.Exists(copied.SqliteStatePath));
            Assert.False(File.Exists(copied.ModelFunctionCatalogPath));

            var expected = await UpdateDescription(baseline, baselineStore);
            var actual = await UpdateDescription(copied, new BacklogStore(copied.BacklogStorePath));
            Assert.Equal(expected, actual);
            Assert.Equal(("Original title", "  alpha\tbeta\r\ngamma --keep-this-word  ",
                BacklogItemStatus.Done, "high", $"Updated: [<id>] Original title{Environment.NewLine}"), actual);
            Assert.False(File.Exists(copied.SqliteStatePath));
            Assert.False(File.Exists(copied.ModelFunctionCatalogPath));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(copiedRoot);
            SharedTestSupport.RemoveTempDirectory(baselineRoot);
        }
    }

    [Xunit.Fact]
    public async Task CopyTo_MutatedCopy_LeavesOtherCopiesAndTemplateEmpty()
    {
        var roots = new[] { CreateTempDirectory(), CreateTempDirectory(), CreateTempDirectory() };
        try
        {
            CliBacklogUpdateWorkspaceTemplate.CopyTo(roots[0]);
            CliBacklogUpdateWorkspaceTemplate.CopyTo(roots[1]);
            var first = new BacklogStore(OrchestratorWorkspace.ForDirectory(roots[0]).BacklogStorePath);
            await first.AddAsync("Only in the first copy", "private body");
            Assert.Single(await first.ListAsync(includeAll: true));

            // A copy made before the write and one made afterwards must both remain empty.
            CliBacklogUpdateWorkspaceTemplate.CopyTo(roots[2]);
            foreach (var root in roots.Skip(1))
            {
                var store = BacklogStore.OpenReadOnly(OrchestratorWorkspace.ForDirectory(root).BacklogStorePath);
                Assert.Empty(await store.ListAsync(includeAll: true));
            }
        }
        finally
        {
            foreach (var root in roots)
                SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private async Task<(string Title, string Body, BacklogItemStatus Status, string? Priority, string Output)>
        UpdateDescription(OrchestratorWorkspace workspace, BacklogStore store)
    {
        var item = await store.AddAsync("Original title", "Original body");
        var output = ExecuteCliAndCapture(
            ["backlog-update", item.Id[..8], "--description", "  alpha\tbeta\r\ngamma --keep-this-word  ",
                "--priority", "high", "--status", "done"], new AgentOrchestratorKernel(), workspace);
        var updated = await store.GetByExactIdAsync(item.Id);
        Assert.NotNull(updated);
        return (updated.Title, updated.Body, updated.Status, updated.Priority,
            output.Replace(item.Id, "<id>", StringComparison.Ordinal));
    }

    private static (string Type, string Name, string Table, string? Sql)[] ReadSchema(string path)
    {
        using var connection = OpenReadOnly(path);
        Assert.Equal(StoreSchemaState.Current, StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.Backlog));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name, tbl_name";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows.ToArray();
    }

    private static string ReadJournalMode(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
