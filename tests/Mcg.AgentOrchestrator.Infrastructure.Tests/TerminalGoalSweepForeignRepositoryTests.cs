using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class TerminalGoalSweepForeignRepositoryTests
{
    [Fact]
    public void Canonical_temp_repository_cannot_reclaim_a_goal_held_by_an_unrelated_store()
    {
        var temp = Directory.CreateTempSubdirectory("foreign-goal-root-").FullName;
        try
        {
            var repoA = Path.Combine(temp, "repo-a");
            var repoB = Path.Combine(temp, "repo-b");
            Directory.CreateDirectory(repoA);
            Directory.CreateDirectory(repoB);
            var dbA = OrchestratorWorkspace.ForDirectory(repoA).SqliteStatePath;
            var dbB = OrchestratorWorkspace.ForDirectory(repoB).SqliteStatePath;
            StateDbMigrations.EnsureUpToDate(dbA);
            StateDbMigrations.EnsureUpToDate(dbB);
            var goal = GoalId.New();
            using (var connection = new SqliteConnection($"Data Source={dbB};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, 'Active', 'held', '2026-01-01', '{}')";
                command.Parameters.AddWithValue("$id", goal.Value);
                command.ExecuteNonQuery();
            }

            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var root = DotnetBuildEnvironmentManager.CreateAttempt(
                goal, "acceptance", storageRoot: storage, repositoryRoot: repoB).RootPath;
            var orphan = DotnetBuildEnvironmentManager.CreateAttempt(
                GoalId.New(), "acceptance", storageRoot: storage, repositoryRoot: repoA).RootPath;
            File.WriteAllText(Path.Combine(root, "keep.bin"), "repo B bytes");
            var before = Snapshot(root);
            var registryA = new OrchestratorProjectRegistry(Path.Combine(temp, "registry-a"));
            Assert.DoesNotContain(dbB, TerminalGoalSweep.GetSharedGoalStorePaths(
                dbA, out _, repoA, registryA)!);

            var cliSweep = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                dbA, storage, new TerminalGoalSweep.OwnedRootSweepState(), _ => false,
                usesSharedStorageRoot: true, canonicalRepoRoot: repoA, projectRegistry: registryA,
                reclaimGoalRoots: false, requireSharedRootOwnership: true);
            var loopSweep = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                dbA, storage, new TerminalGoalSweep.OwnedRootSweepState(), _ => false,
                usesSharedStorageRoot: true, canonicalRepoRoot: repoA, projectRegistry: registryA,
                requireSharedRootOwnership: true);

            Assert.Empty(cliSweep.ReclaimedGoalRoots!);
            Assert.Single(loopSweep.ReclaimedGoalRoots!);
            Assert.Contains(orphan, loopSweep.ReclaimedGoalRoots!);
            Assert.False(Directory.Exists(orphan));
            Assert.Equal(before, Snapshot(root));
        }
        finally
        {
            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, storage).Count == 25)
            {
            }
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];
}
