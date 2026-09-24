using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class CliAttentionNextReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task PureReadFormsCompleteWhileAnExternalWriterIsHeld()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Held writer attention goal");
            await repository.SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ($id, $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$id", "held-writer-attention-outbox");
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                await insert.ExecuteNonQueryAsync();
            }

            var before = await ReadStateRowsAsync(workspace.SqliteStatePath);
            var beforeItems = await File.ReadAllBytesAsync(itemsDb);
            await using var writer = OpenStateConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                foreach (var args in new[]
                {
                    new[] { "attention" },
                    new[] { "attention", "list" },
                    new[] { "attention", "show" },
                    new[] { "attention", "show", "--all" },
                    new[] { "attention", "show", goal.Id.Value[..8] },
                    new[] { "next", "--full", goal.Id.Value[..8] }
                })
                {
                    var result = await RunQueryAsync(root, args);
                    Xunit.Assert.Equal(0, result.ExitCode);
                    Xunit.Assert.Equal(string.Empty, result.Error);
                    if (args[0] == "attention")
                    {
                        var expected = args.Length == 3 && args[2] == "--all"
                            ? CliAttentionNextGoldenFixtures.EmptyAttentionHistory
                            : args.Length == 3
                                ? CliAttentionNextGoldenFixtures.EmptyGoalAttention(goal.Id.Value)
                                : CliAttentionNextGoldenFixtures.EmptyAttention;
                        Xunit.Assert.Equal(expected, result.Output);
                    }
                    else
                        Xunit.Assert.Contains($"Goal diagnostics {goal.Id.Value[..8]}", result.Output);
                }
                foreach (var args in new[]
                {
                    new[] { "attention", "show", "missing" },
                    new[] { "next", "--full", "missing" }
                })
                {
                    var result = await RunQueryAsync(root, args);
                    var fixture = args[0] == "attention"
                        ? CliAttentionNextGoldenFixtures.UnknownAttentionGoal
                        : CliAttentionNextGoldenFixtures.UnknownNextFullGoal;
                    Xunit.Assert.Equal(fixture.ExitCode, result.ExitCode);
                    Xunit.Assert.Equal(fixture.Stdout, result.Output);
                    Xunit.Assert.Equal(fixture.Stderr, result.Error);
                }
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

    internal static async Task<(int ExitCode, string Output, string Error)> RunQueryAsync(string root, string[] args)
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
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start query process.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
