using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Real-process coverage is parallel-safe: every database and writer belongs to this workspace.
public sealed class CliGoalDiagnosticsReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ExternalWriterHeld_ReadsDiagnosticsWithoutChangingRows()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Held writer diagnostics goal");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ($id, $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$id", "held-writer-diagnostics-outbox");
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                Xunit.Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            var before = await ReadStateRowsAsync(workspace.SqliteStatePath);
            Xunit.Assert.Equal(2, before.Length);
            Xunit.Assert.Contains(before, row => row.StartsWith("goal:" + goal.Id.Value + ":", StringComparison.Ordinal));
            Xunit.Assert.Contains(before, row => row.StartsWith("outbox:held-writer-diagnostics-outbox:", StringComparison.Ordinal));
            var beforeItems = await File.ReadAllBytesAsync(itemsDb);
            await using var writer = OpenStateConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                // Completion of this command is the gate: the write lock is held before launch.
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                var result = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(
                    root, ["goal-diagnostics", goal.Id.Value[..8]]);
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Equal(string.Empty, result.Error);
                Xunit.Assert.Contains(result.Output.Split(Environment.NewLine),
                    line => line.StartsWith("Goal diagnostics abc10000", StringComparison.Ordinal));
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }
            Xunit.Assert.Equal(before, await ReadStateRowsAsync(workspace.SqliteStatePath));
            Xunit.Assert.Equal(beforeItems, await File.ReadAllBytesAsync(itemsDb));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());

    private static async Task<string[]> ReadStateRowsAsync(string path)
    {
        await using var connection = OpenStateConnection(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'goal:' || id || ':' || version || ':' || snapshot_json FROM goals UNION ALL SELECT 'outbox:' || id || ':' || kind || ':' || payload_json || ':' || created_at FROM state_outbox ORDER BY 1";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
}
