using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its stores, workspace, writer connection and child processes.
public sealed class CliInspectionReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ExactForms_HeldWriterPreservesOutputAndStateRows()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            await SeedWorkspaceAsync(workspace, includeOutbox: true);
            var before = await ReadStateRowsAsync(workspace.SqliteStatePath);
            await using var writer = OpenStateConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                foreach (var args in CliInspectionReadOnlyRouteTests.ExactForms())
                {
                    var result = await RunQueryAsync(root, args);
                    Xunit.Assert.Equal(0, result.ExitCode);
                    Xunit.Assert.Equal(string.Empty, result.StandardError);
                    Xunit.Assert.NotEmpty(result.StandardOutput);
                    if (args[0] == "goals")
                    {
                        Xunit.Assert.Contains("First inspection goal", result.StandardOutput);
                        Xunit.Assert.Contains("Second inspection goal", result.StandardOutput);
                    }
                    if (args[0] == "model-outcomes")
                        Xunit.Assert.Contains("inspection-model", result.StandardOutput);
                    if (args[0] == "backlog-view")
                        Xunit.Assert.Contains("Seeded inspection backlog item", result.StandardOutput);
                    var after = await ReadStateRowsAsync(workspace.SqliteStatePath);
                    Xunit.Assert.Equal(before.Goals, after.Goals);
                    Xunit.Assert.Equal(before.Outbox, after.Outbox);
                }
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task SeedWorkspaceAsync(OrchestratorWorkspace workspace, bool includeOutbox)
    {
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(CliInspectionReadOnlyRouteTests.CreateInspectionSeed());
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await new BacklogStore(workspace.BacklogStorePath).AddAsync(
            "Seeded inspection backlog item", "Inspection body");
        await using var connection = OpenStateConnection(workspace.SqliteStatePath);
        await connection.OpenAsync();
        await using (var model = connection.CreateCommand())
        {
            model.CommandText = """
                INSERT INTO model_fit_history
                    (goal_id, task_id, role, provider_name, model_name, complexity,
                     task_shape, outcome, self_rating, timestamp)
                VALUES ('abc10000aaaaaaaaaaaaaaaaaaaaaaaa', 'inspection-task', 'Developer',
                        'OpenAI', 'inspection-model', 'Simple', 'inspection query',
                        'Completed', 'adequate', '2026-09-24T00:00:00.0000000+00:00')
                """;
            Xunit.Assert.Equal(1, await model.ExecuteNonQueryAsync());
        }
        if (includeOutbox)
        {
            await using var outbox = connection.CreateCommand();
            outbox.CommandText = """
                INSERT INTO state_outbox (id, kind, payload_json, created_at)
                VALUES ('held-writer-inspection-outbox', $kind, '{}', '2026-09-24T00:00:00.0000000+00:00')
                """;
            outbox.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
            Xunit.Assert.Equal(1, await outbox.ExecuteNonQueryAsync());
        }
        Xunit.Assert.Equal(2, (await repository.ListGoalMetadataAsync()).Count);
        Xunit.Assert.Single(await repository.ListModelFitHistoryAsync());
        Xunit.Assert.Equal(includeOutbox ? 1 : 0,
            (await repository.ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)).Count);
    }

    internal static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    internal static async Task<(byte[] Goals, byte[] Outbox)> ReadStateRowsAsync(string path)
    {
        await using var connection = OpenStateConnection(path);
        await connection.OpenAsync();
        return (await ReadRowsAsync(connection, "SELECT * FROM goals ORDER BY id"),
            await ReadRowsAsync(connection, "SELECT * FROM state_outbox ORDER BY id"));
    }

    private static async Task<byte[]> ReadRowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = reader.IsDBNull(index)
                    ? null
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
            rows.Add(values);
        }
        return JsonSerializer.SerializeToUtf8Bytes(rows);
    }

    private static async Task<CliChildProcessResult> RunQueryAsync(string root, string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        // The shared runner closes stdin, drains both output pipes and waits for the exit artifact.
        // Keep only runtime locations so provider credentials cannot select a different startup path.
        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
                start.Environment[name] = value;
        }
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        return await CliChildProcessRunner.RunAsync(start);
    }
}
