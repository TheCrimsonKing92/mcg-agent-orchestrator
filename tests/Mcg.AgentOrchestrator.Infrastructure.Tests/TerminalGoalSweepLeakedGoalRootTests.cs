using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class TerminalGoalSweepLeakedGoalRootTests
{
    [Fact]
    public void Sweep_reclaims_only_absent_goal_with_dead_owner_and_readable_lease()
    {
        var temp = Directory.CreateTempSubdirectory("goal-reclaim-").FullName;
        try
        {
            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var db = Path.Combine(temp, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var absent = GoalId.New();
            var held = GoalId.New();
            var withoutLease = GoalId.New();
            var absentRoot = DotnetBuildEnvironmentManager.CreateAttempt(absent, "acceptance", storageRoot: storage).RootPath;
            var heldRoot = DotnetBuildEnvironmentManager.CreateAttempt(held, "acceptance", storageRoot: storage).RootPath;
            var noLeaseRoot = DotnetBuildEnvironmentManager.ResolveGoalEnvironment(withoutLease, storage).RootPath;
            using (var connection = new SqliteConnection($"Data Source={db}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, 'Completed', 'held', '2026-01-01', '{}')";
                command.Parameters.AddWithValue("$id", held.Value);
                command.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(heldRoot, "keep.bin"), "held bytes");
            File.WriteAllText(Path.Combine(noLeaseRoot, "keep.bin"), "no lease bytes");
            var heldBytes = Snapshot(heldRoot);
            var noLeaseBytes = Snapshot(noLeaseRoot);

            var result = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                db, storage, new TerminalGoalSweep.OwnedRootSweepState(), _ => false);

            Assert.False(Directory.Exists(absentRoot));
            Assert.Equal(heldBytes, Snapshot(heldRoot));
            Assert.Equal(noLeaseBytes, Snapshot(noLeaseRoot));
            Assert.Contains(absentRoot, result.ReclaimedGoalRoots!);
            Assert.Single(result.OperatorEvents.Where(line => line.Contains("SWEEP_GOAL_ROOT_RECLAIMED", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];
}
