using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class TerminalGoalSweepSharedGoalRootTests
{
    [Fact]
    public void Default_store_sweep_preserves_goal_held_by_registered_project_store()
    {
        var temp = Directory.CreateTempSubdirectory("shared-goal-root-").FullName;
        try
        {
            var repoRoot = Path.Combine(temp, "default-repo");
            var projectRoot = Path.Combine(temp, "project-repo");
            Directory.CreateDirectory(repoRoot);
            Directory.CreateDirectory(projectRoot);
            var registry = new OrchestratorProjectRegistry(Path.Combine(temp, "registry"));
            registry.CreateProject("other", projectRoot);
            var defaultDb = OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath;
            var projectDb = OrchestratorWorkspace.ForProject("other", projectRoot).SqliteStatePath;
            StateDbMigrations.EnsureUpToDate(defaultDb);
            StateDbMigrations.EnsureUpToDate(projectDb);

            var goal = GoalId.New();
            using (var connection = new SqliteConnection($"Data Source={projectDb};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, 'Active', 'project goal', '2026-01-01', '{}')";
                command.Parameters.AddWithValue("$id", goal.Value);
                command.ExecuteNonQuery();
            }

            var storage = new DotnetBuildStorageRoot(Path.Combine(temp, "isolated"));
            var root = DotnetBuildEnvironmentManager.CreateAttempt(
                goal, "acceptance", storageRoot: storage).RootPath;
            var orphanRoot = DotnetBuildEnvironmentManager.CreateAttempt(
                GoalId.New(), "acceptance", storageRoot: storage).RootPath;
            File.WriteAllText(Path.Combine(root, "keep.bin"), "project goal bytes");
            var before = Snapshot(root);

            var result = TerminalGoalSweep.ReapOwnedBuildRootsCore(
                defaultDb, storage, new TerminalGoalSweep.OwnedRootSweepState(),
                _ => false, usesSharedStorageRoot: true,
                canonicalRepoRoot: repoRoot, projectRegistry: registry);

            Assert.Single(result.ReclaimedGoalRoots!);
            Assert.Contains(orphanRoot, result.ReclaimedGoalRoots!);
            Assert.False(Directory.Exists(orphanRoot));
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
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];
}
