using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: the test owns its stores, writer connection and child process.
public sealed class CliEpicPlanReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task PlanQuery_HeldWriter_PreservesOutputAndStateRows()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
            var epic = await portfolio.AddEpicAsync("Board");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Unplanned member");
            await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('held-writer-plan-outbox', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-10-24T00:00:00.0000000+00:00");
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            var before = await ReadStateRowsAsync(workspace.SqliteStatePath);
            await using var writer = OpenStateConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            CliChildProcessResult result;
            try
            {
                result = await RunQueryAsync(root, ["epic-plan", "Board"]);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.StandardError);
                Assert.Contains("Board", result.StandardOutput);
                Assert.Contains(goal.Id.Value, result.StandardOutput);
                var after = await ReadStateRowsAsync(workspace.SqliteStatePath);
                Assert.Equal(before.Goals, after.Goals);
                Assert.Equal(before.Outbox, after.Outbox);
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var writerOutput = CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                ["epic-plan", "Board"], repository, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref current, skipReadOnlyRoute: true)));
            Assert.Equal(writerOutput, result.StandardOutput);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());

    private static async Task<(byte[] Goals, byte[] Outbox)> ReadStateRowsAsync(string path)
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
        Assert.Single(rows);
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
        // Only runtime locations are inherited; provider credentials and behavior switches
        // cannot bypass the startup path this child is verifying.
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
