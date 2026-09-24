using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

public sealed class CliGoalEventsStatusReadOnlyTests
{
    [Xunit.Fact]
    public async Task SingleGoalQueriesCompleteWhileExternalWriterIsHeldWithoutChangingState()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Held writer query goal");
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl"),
                "{\"eventKind\":\"query-test\"}\n");
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ($id, $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$id", "held-writer-outbox");
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                await insert.ExecuteNonQueryAsync();
            }

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
                var events = await RunQueryAsync(root, "goal-events", goal.Id.Value[..8]);
                Xunit.Assert.Equal(0, events.ExitCode);
                Xunit.Assert.Equal("{\"eventKind\":\"query-test\"}" + Environment.NewLine, events.Output);
                Xunit.Assert.Equal(string.Empty, events.Error);

                var status = await RunQueryAsync(root, "status", goal.Id.Value[..8]);
                Xunit.Assert.Equal(0, status.ExitCode);
                Xunit.Assert.Contains(goal.Id.Value, status.Output, StringComparison.Ordinal);
                Xunit.Assert.Contains(goal.Objective, status.Output, StringComparison.Ordinal);
                Xunit.Assert.Equal(string.Empty, status.Error);
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }

            Xunit.Assert.Equal(before, await ReadStateRowsAsync(workspace.SqliteStatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());

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

    private static async Task<(int ExitCode, string Output, string Error)> RunQueryAsync(
        string root, string verb, string prefix)
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
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add(prefix);
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start query process.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
