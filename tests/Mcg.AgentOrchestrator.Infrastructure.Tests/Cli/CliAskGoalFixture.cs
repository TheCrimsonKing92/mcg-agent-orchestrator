using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each fixture owns its repository, registry, data directory, stores, and child environment.
internal sealed class CliAskGoalFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-ask-goal-").FullName;
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOrchestratorStateRepository Repository => new(Workspace.SqliteStatePath);
    internal static readonly GoalId CurrentId = new("abc10000aaaaaaaaaaaaaaaaaaaaaaaa");
    internal static readonly GoalId OtherId = new("abc20000bbbbbbbbbbbbbbbbbbbbbbbb");

    internal CliAskGoalFixture()
    {
        Workspace = OrchestratorWorkspace.ForDirectory(Root, null, null,
            new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), Path.Combine(Root, "data")));
    }

    internal async Task SeedAsync()
    {
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Current goal task", AgentRole.Developer);
        kernel.CreateGoal(OtherId, "Other goal", [new TaskSpec(TaskId.New(), "Other goal task", AgentRole.Tester)]);
        kernel.CreateGoal(CurrentId, "Current goal", [task]);
        kernel.RequestHumanInput(CurrentId, task.Id, "Existing task question");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Active,
                CreatedAt = DateTimeOffset.Parse(goal.Id == CurrentId.Value
                    ? "2026-01-02T00:00:00Z" : "2026-01-01T00:00:00Z")
            }).ToArray()
        });
        await Repository.SaveAsync(kernel);
        // A generic outbox drain would claim/remove this row or fail on its payload.
        using var connection = OpenConnection(SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ('ask-goal-outbox', $kind, '{}', '2026-01-01T00:00:00Z')";
        command.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
        Xunit.Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal async Task<OrchestratorSnapshot> SnapshotAsync() => (await Repository.LoadAsync()).ExportSnapshot();

    internal string StateRows()
    {
        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = list.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        var result = new SortedDictionary<string, string[]>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values));
            }
            result.Add(table, rows.Order(StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(result);
    }

    internal string OutboxRows()
    {
        using var connection = OpenConnection(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, payload_json, created_at FROM state_outbox ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return string.Join('\n', rows);
    }

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Workspace.SqliteStatePath, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    internal Task<CliChildProcessResult> RunAsync(string[] args, bool poisonCleanup = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in new[] { "--repository-root", Root, "--expected-state-directory", Workspace.OrchestratorDirectory, "--disposable-fixture" }.Concat(args))
            start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "DOTNET_ROOT",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = Root;
        start.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(Root, "registry");
        start.Environment[OrchestratorDataRoot.EnvironmentVariable] = Path.Combine(Root, "data");
        start.Environment[OrchestratorHome.EnvironmentVariable] = Root;
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(Root, "dotnet");
        if (poisonCleanup) start.Environment["WorktreeCleanup__EscalationThreshold"] = "not-an-integer";
        return CliChildProcessRunner.RunAsync(start);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
