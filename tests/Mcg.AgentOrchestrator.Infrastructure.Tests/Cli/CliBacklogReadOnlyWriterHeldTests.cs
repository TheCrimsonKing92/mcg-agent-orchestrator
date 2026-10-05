using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: this case owns its databases, writer connection and CLI children.
public sealed class CliBacklogReadOnlyWriterHeldTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public async Task ReadForms_HeldWriterPreservesOutputGoalsAndPendingOutbox()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CliBacklogReadOnlyRouteTests.CreateSeedAsync(root);
            var statePath = seed.Workspace.SqliteStatePath;
            await using (var connection = OpenStateConnection(statePath))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('held-writer-backlog-outbox', $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-10-05T00:00:00.0000000+00:00");
                Xunit.Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                await using var count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM state_outbox WHERE kind = $kind AND quarantined_at IS NULL AND processing_token IS NULL";
                count.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                Xunit.Assert.Equal(1L, await count.ExecuteScalarAsync());
            }

            var before = await ReadStateRowsAsync(statePath);
            await using var writer = OpenStateConnection(statePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                foreach (var args in CliBacklogReadOnlyRouteTests.ExplicitForms(seed.Prerequisite.Id[..8]))
                {
                    var result = await RunQueryAsync(root, args);
                    Xunit.Assert.Equal(0, result.ExitCode);
                    Xunit.Assert.Equal(string.Empty, result.StandardError);
                    Xunit.Assert.NotEmpty(result.StandardOutput);
                    if (args[0] == "backlog-show")
                    {
                        Xunit.Assert.Contains($"Owner:   {CliBacklogReadOnlyRouteTests.LinkedGoalId}", result.StandardOutput);
                        Xunit.Assert.Contains("authority=authoritative", result.StandardOutput);
                    }
                    var after = await ReadStateRowsAsync(statePath);
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
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
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
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        // Allow only runtime locations; the shared runner closes stdin and drains both pipes.
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
