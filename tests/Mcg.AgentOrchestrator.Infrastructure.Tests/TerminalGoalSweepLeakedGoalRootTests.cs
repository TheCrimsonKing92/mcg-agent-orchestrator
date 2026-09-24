using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class TerminalGoalSweepLeakedGoalRootTests
{
    [Fact]
    public void Sweep_does_not_reclaim_an_ambient_root_from_an_unrelated_store()
    {
        var temp = Directory.CreateTempSubdirectory("goal-unowned-").FullName;
        try
        {
            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var db = Path.Combine(temp, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var unownedRoot = DotnetBuildEnvironmentManager.CreateAttempt(
                GoalId.New(), "acceptance", storageRoot: storage).RootPath;
            File.WriteAllText(Path.Combine(unownedRoot, "keep.bin"), "unowned bytes");
            var before = Snapshot(unownedRoot);

            Assert.False(TerminalGoalSweep.IsCanonicalGoalRootStore(db));
            var result = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                db, storage, new TerminalGoalSweep.OwnedRootSweepState(),
                _ => false, usesSharedStorageRoot: true);

            Assert.True(Directory.Exists(unownedRoot));
            Assert.Equal(before, Snapshot(unownedRoot));
            Assert.Empty(result.ReclaimedGoalRoots!);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Sweep_retains_live_owner_and_reclaims_dead_owner_after_machine_rename()
    {
        var temp = Directory.CreateTempSubdirectory("goal-owner-").FullName;
        try
        {
            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var db = Path.Combine(temp, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var liveRoot = DotnetBuildEnvironmentManager.CreateAttempt(
                GoalId.New(), "acceptance", storageRoot: storage).RootPath;
            var deadRoot = DotnetBuildEnvironmentManager.CreateAttempt(
                GoalId.New(), "acceptance", storageRoot: storage).RootPath;
            var leasePath = Path.Combine(deadRoot, "lease", "lease.json");
            var lease = JsonNode.Parse(File.ReadAllText(leasePath))!;
            lease["machineName"] = "prior-machine-name";
            lease["ownerProcessId"] = int.MaxValue;
            File.WriteAllText(leasePath, lease.ToJsonString());
            var liveBytes = Snapshot(liveRoot);

            var result = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                db, storage, new TerminalGoalSweep.OwnedRootSweepState(),
                pid => pid == Environment.ProcessId);

            Assert.Equal(liveBytes, Snapshot(liveRoot));
            Assert.False(Directory.Exists(deadRoot));
            Assert.Contains(deadRoot, result.ReclaimedGoalRoots!);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

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
            using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
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
