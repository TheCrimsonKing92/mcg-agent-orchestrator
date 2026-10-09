using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class DefaultStateMoverFixture : IDisposable
{
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
    internal string Repository => Path.Combine(Root, "repo");
    internal string Source => Path.Combine(Repository, ".orchestrator");
    internal string DataRoot => Path.Combine(Root, "data");
    internal string Destination => Path.Combine(DataRoot, DefaultProjectStateLocation.DestinationSubpath(Repository));
    internal OrchestratorProjectRegistry Registry { get; }
    internal static DateTimeOffset Timestamp => new(2026, 10, 8, 12, 30, 0, TimeSpan.Zero);
    internal string Backup => Path.Combine(Repository, ".orchestrator.backup-20261008T123000Z");

    internal DefaultStateMoverFixture()
    {
        Directory.CreateDirectory(Source);
        Registry = new OrchestratorProjectRegistry(Path.Combine(Root, "registry"), DataRoot);
    }

    internal async Task Seed(bool activeGoal = false, bool activeDispatch = false, bool activeProcess = false)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fixture board", [new TaskSpec(TaskId.New(), "Fixture task", AgentRole.Developer)]);
        if (!activeGoal) kernel.CancelGoal(goal.Id, "Fixture terminal goal");
        if (activeDispatch || activeProcess)
        {
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item with
                {
                    Tasks = item.Tasks.Select(task => task with
                    {
                        Status = activeDispatch ? WorkTaskStatus.Running : task.Status,
                        LastProcess = activeProcess ? new TaskProcessSnapshot(12345, "fixture", Repository,
                            "fixture.out", "fixture.err", "fixture.exit", Timestamp, null, null) : task.LastProcess
                    }).ToArray()
                }).ToArray()
            });
        }
        var path = Path.Combine(Source, "state.db");
        StateDbMigrations.EnsureUpToDate(path);
        await new SqliteOrchestratorStateRepository(path).SaveAsync(kernel);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Source, "backlog.db"), Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE fixture_backlog (body TEXT); INSERT INTO fixture_backlog VALUES ('retained');";
            command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Path.Combine(Source, "logs"));
        File.WriteAllText(Path.Combine(Source, "logs", "a.jsonl"), "{\"fixture\":true}\n");
        Directory.CreateDirectory(Path.Combine(Source, "goal-events", "nested"));
        File.WriteAllBytes(Path.Combine(Source, "goal-events", "nested", "event.bin"), [0, 1, 127, 255]);
        Directory.CreateDirectory(Path.Combine(Source, "empty", "nested"));
        File.WriteAllText(Path.Combine(Source, "agents.json"), "[]");
    }

    internal DefaultStateMover Mover(IConductorLockProbe? lockProbe = null,
        Func<string, bool>? stopPending = null, Action<string, string>? copyFile = null,
        Action<string, string>? createLink = null) =>
        new(Repository, Registry, lockProbe, stopPending, copyFile, createLink: createLink, utcNow: () => Timestamp);

    internal static string[] Snapshot(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Directory.Exists(path)
                ? "D:" + Path.GetRelativePath(directory, path)
                : "F:" + Path.GetRelativePath(directory, path) + ":" + HashFile(path))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static string HashFile(string path)
    {
        // Match the lock probe's sharing contract while a real lease is held.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static long GoalCount(string stateDirectory)
    {
        using var connection = StateDbConnectionFactory.Open(Path.Combine(stateDirectory, "state.db"), StateDbConnectionProfile.FastFailRead);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM goals";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (new DirectoryInfo(Source).LinkTarget is not null) Directory.Delete(Source);
        SharedTestSupport.RemoveTempDirectory(Root);
    }
}
