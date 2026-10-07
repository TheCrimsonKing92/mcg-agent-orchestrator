using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its database, writer connection and child process root.
public sealed class CliPlanReportReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ExplicitForms_HeldWriterPreservesOutputAndEveryStateRow()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = CliSingleGoalReportReadOnlyRouteTests.CreateReportSeed(root, includeOther: false);
            var goal = Xunit.Assert.Single(kernel.Goals);
            Xunit.Assert.Contains(goal.Tasks, task => task.LastVerification?.Succeeded is true);
            Xunit.Assert.All(goal.Tasks, task =>
            {
                Xunit.Assert.False(WorkerProfileDispatcher.IsTaskRetryDeferred(
                    task, DateTimeOffset.Parse("2026-09-24T00:00:00+00:00"), out _));
                Xunit.Assert.False(DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
                    goal, task.Id, "OpenAI", DateTimeOffset.Parse("2026-09-24T00:00:00+00:00"), out _));
            });
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('held-writer-plan-report-outbox', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                Xunit.Assert.Equal(1, await insert.ExecuteNonQueryAsync());
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
                foreach (var args in new string[][]
                {
                    ["retention-plan", goal.Id.Value[..8]],
                    ["supervisor", goal.Id.Value[..8]]
                })
                {
                    var result = await RunQueryAsync(root, args);
                    Xunit.Assert.Equal(0, result.ExitCode);
                    Xunit.Assert.Equal(string.Empty, result.StandardError);
                    var header = args[0] == "retention-plan" ? "Retention plan goal: " : "Supervisor goal: ";
                    Xunit.Assert.Contains(header + goal.Id.Value[..8], result.StandardOutput);
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
        // The helper closes stdin and drains both output pipes; inherit only runtime locations.
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
