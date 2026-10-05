using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace and stores; both routes see the same seed and paths.
public sealed class CliInspectionRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ExactForms_PreserveWriterOutputIncludingCleanupDebt(bool includeCleanupDebt)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            await CliInspectionReadOnlyWriterHeldTests.SeedWorkspaceAsync(workspace, includeOutbox: false);
            if (includeCleanupDebt)
                await SeedCleanupDebtAsync(workspace);
            var before = await CliInspectionReadOnlyWriterHeldTests.ReadStateRowsAsync(workspace.SqliteStatePath);
            foreach (var args in CliInspectionReadOnlyRouteTests.ExactForms())
            {
                // This assertion is the negative control at the pre-change HEAD: both routes
                // otherwise fall back to the writer and equal output alone would pass there.
                Xunit.Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var expected = Execute(args, workspace, skipReadOnlyRoute: true);
                var actual = Execute(args, workspace, skipReadOnlyRoute: false);
                Xunit.Assert.NotEmpty(expected);
                Xunit.Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
                if (args[0] == "goals")
                {
                    if (includeCleanupDebt)
                        Xunit.Assert.Contains("WARNING: stale worktree cleanup escalated=1", expected);
                    else
                        Xunit.Assert.DoesNotContain("WARNING: stale worktree cleanup", expected);
                }
                if (args[0] == "model-outcomes")
                    Xunit.Assert.Contains("inspection-model", expected);
                if (args[0] == "backlog-view")
                    Xunit.Assert.Contains("Seeded inspection backlog item", expected);
                var after = await CliInspectionReadOnlyWriterHeldTests.ReadStateRowsAsync(workspace.SqliteStatePath);
                Xunit.Assert.Equal(before.Goals, after.Goals);
                Xunit.Assert.Equal(before.Outbox, after.Outbox);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Xunit.Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal, skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private static async Task SeedCleanupDebtAsync(OrchestratorWorkspace workspace)
    {
        await using var connection = CliInspectionReadOnlyWriterHeldTests.OpenStateConnection(workspace.SqliteStatePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO worktree_cleanup_backoff (path, skip_until_utc, reason)
            VALUES ($path, '2099-01-01T00:00:00.0000000+00:00', 'seeded cleanup debt');
            INSERT INTO worktree_cleanup_journal
                (path, first_seen_utc, last_seen_utc, last_operation, last_reason, skip_count, escalated_at_utc)
            VALUES ($path, '2026-09-24T00:00:00.0000000+00:00', '2026-09-24T00:00:00.0000000+00:00',
                    'remove', 'seeded cleanup debt', 3, '2026-09-24T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$path", Path.Combine(workspace.ExecutionDirectory,
            GoalWorktrees.DirectoryName, "inspection-cleanup-debt"));
        Xunit.Assert.Equal(2, await command.ExecuteNonQueryAsync());
    }
}
