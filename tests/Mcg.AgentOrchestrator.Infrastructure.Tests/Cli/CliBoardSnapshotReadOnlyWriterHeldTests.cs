using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every case owns its workspace, writer connection and CLI child.
public sealed class CliBoardSnapshotReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("loop-health")]
    [InlineData("durations")]
    [InlineData("dispatch-value")]
    [InlineData("operator-inbox")]
    [InlineData("brief")]
    public async Task Query_HeldWriterLeavesGoalsAndOutboxIdentical(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel(new FixedClock());
            var goal = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Held writer query",
                [new TaskSpec(TaskId.New(), "Read the task brief", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await using (var seed = OpenConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('held-writer-board-outbox', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-10-08T00:00:00.0000000+00:00");
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await using var writer = OpenConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            var goalsBefore = await ReadRowsAsync(writer, "SELECT * FROM goals ORDER BY id");
            var outboxBefore = await ReadRowsAsync(writer, "SELECT * FROM state_outbox ORDER BY id");
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                string[] args = verb == "brief" ? [verb, goal.Id.Value[..8], "1"] : [verb];
                Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
                var result = await RunQueryAsync(root, args);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.StandardError);
                Assert.NotEmpty(result.StandardOutput);
                // Read through another connection so the writer's transaction cannot hide writes.
                await using var reader = OpenConnection(workspace.SqliteStatePath);
                await reader.OpenAsync();
                Assert.Equal(goalsBefore, await ReadRowsAsync(reader,
                    "SELECT * FROM goals ORDER BY id"));
                Assert.Equal(outboxBefore, await ReadRowsAsync(reader,
                    "SELECT * FROM state_outbox ORDER BY id"));
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

    private static SqliteConnection OpenConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());

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
                values[index] = reader.IsDBNull(index) ? null :
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
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
        // An explicit runtime-location allow-list keeps provider and handoff settings out.
        // CliChildProcessRunner closes stdin, drains both pipes and bounds hangs only.
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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }
}
