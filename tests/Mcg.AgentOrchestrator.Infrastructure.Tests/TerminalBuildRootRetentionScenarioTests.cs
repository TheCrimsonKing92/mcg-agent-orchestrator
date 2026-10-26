using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the repository, storage namespace, project registry and SQLite stores are invocation-owned.
public sealed class TerminalBuildRootRetentionScenarioTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("retention-scenario-").FullName;
    private readonly List<string> _roots = [];
    private readonly OrchestratorProjectRegistry _projects;
    private readonly TerminalGoalSweep.OwnedRootSweepState _state = new();
    private string Repo => Path.Combine(_temp, "repo");
    private string Db => Path.Combine(Repo, ".orchestrator", "state.db");
    private string PolicyFile => Path.Combine(Repo, ".orchestrator", "conductor-policy.json");
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));

    public TerminalBuildRootRetentionScenarioTests()
    {
        _projects = new OrchestratorProjectRegistry(Path.Combine(_temp, "registry"), Path.Combine(_temp, "data"));
        StateDbMigrations.EnsureUpToDate(Db);
        var now = DateTimeOffset.UtcNow;
        var statuses = new[] { GoalStatus.Completed, GoalStatus.Completed, GoalStatus.Completed,
            GoalStatus.Cancelled, GoalStatus.Parked, GoalStatus.Active, GoalStatus.Completed };
        for (var index = 0; index < statuses.Length; index++)
        {
            var goal = new GoalId($"{index + 1:00000000}000000000000000000000000");
            var root = DotnetBuildEnvironmentManager.CreateAttempt(goal, "acceptance",
                storageRoot: Storage, repositoryRoot: Repo).RootPath;
            File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[1000]);
            var leasePath = Path.Combine(root, "lease", "lease.json");
            var lease = JsonNode.Parse(File.ReadAllText(leasePath))!;
            lease["lastUsedAt"] = now.AddDays(index == 6 ? -1 : -10);
            File.WriteAllText(leasePath, lease.ToJsonString());
            InsertGoal(Db, goal.Value, statuses[index].ToString(), now.AddDays(-10));
            _roots.Add(root);
        }
        for (var index = 0; index < 5; index++)
            Directory.CreateDirectory(Path.Combine(Storage.RootPath, "runs", $"unregistered-{index}"));
        File.WriteAllText(PolicyFile, EnabledPolicy());
    }

    [Fact]
    public void CanonicalSweepReclaimsOnlyOldTerminalRootsAcrossSweeps()
    {
        var protectedSnapshots = _roots.Skip(4).Select(Snapshot).ToArray();
        var first = Sweep();
        Assert.Equal(2, _roots.Take(4).Count(root => !Directory.Exists(root)));
        Assert.False(Directory.Exists(_roots[0]));
        Assert.False(Directory.Exists(_roots[1]));
        var firstSummary = Summary(first);
        Assert.Contains("reclaimed=2", firstSummary);
        Assert.Contains("budget-exhausted:2", firstSummary);
        Assert.Contains("goal-not-terminal:2", firstSummary);
        Assert.Contains("within-window:1", firstSummary);
        Assert.Single(first.OperatorEvents.Where(line => line.StartsWith(
            "SWEEP_OWNED_ROOT_OBSERVED detail=\"observe-only unregistered build roots: count=", StringComparison.Ordinal)));
        Assert.DoesNotContain(first.OperatorEvents, line => line.Contains("observe-only unregistered build root: ", StringComparison.Ordinal));
        Assert.Empty(first.ReclaimedGoalRoots!);
        // Retention also runs on periodic sweeps that do not request the existing orphan reclaimer.
        var second = Sweep(reclaimGoalRoots: false);
        Assert.Contains("reclaimed=2", Summary(second));
        Assert.All(_roots.Take(4), root => Assert.False(Directory.Exists(root)));
        Assert.Empty(second.ReclaimedGoalRoots!);
        var third = Sweep();
        Assert.Contains("reclaimed=0", Summary(third));
        Assert.Empty(third.ReclaimedGoalRoots!);
        for (var index = 4; index < _roots.Count; index++)
            Assert.Equal(protectedSnapshots[index - 4], Snapshot(_roots[index]));
    }

    [Fact]
    public void AbsentKeyOrNonCanonicalStoreChangesNothing()
    {
        var snapshots = _roots.Select(Snapshot).ToArray();
        File.WriteAllText(PolicyFile, string.Join(Environment.NewLine, EnabledPolicy().Split('\n')
            .Where(line => !line.Contains("\"buildRootRetentionDays\"", StringComparison.Ordinal))));
        for (var iteration = 0; iteration < 3; iteration++) AssertUnchanged(Sweep(), snapshots);

        var outsideDb = Path.Combine(_temp, "outside", "state.db");
        StateDbMigrations.EnsureUpToDate(outsideDb);
        File.WriteAllText(Path.Combine(_temp, "outside", "conductor-policy.json"), EnabledPolicy());
        AssertUnchanged(Sweep(outsideDb), snapshots);

        File.WriteAllText(PolicyFile, "not json");
        AssertUnchanged(Sweep(), snapshots);
    }

    [Fact]
    public void UnavailableSharedStoreFailsClosedWithOneSummary()
    {
        var snapshots = _roots.Select(Snapshot).ToArray();
        var otherDb = Path.Combine(Repo, ".orchestrator", "tenants", "other", "state.db");
        StateDbMigrations.EnsureUpToDate(otherDb);
        InsertGoal(otherDb, "ffffffff000000000000000000000000", "unknown-status", DateTimeOffset.UtcNow.AddDays(-10));
        var result = Sweep();
        Assert.Equal("SWEEP_BUILD_ROOT_RETENTION window-days=3 reclaimed=0 bytes=0 skipped=0 reasons=goal-store-unavailable",
            Summary(result));
        for (var index = 0; index < _roots.Count; index++) Assert.Equal(snapshots[index], Snapshot(_roots[index]));
    }

    private TerminalGoalSweepOwnedRootResult Sweep(string? db = null, bool reclaimGoalRoots = true) =>
        TerminalGoalSweep.ReapOwnedBuildRootsCore(db ?? Db, Storage, _state,
            _ => false, usesSharedStorageRoot: true, canonicalRepoRoot: Repo, projectRegistry: _projects,
            reclaimGoalRoots: reclaimGoalRoots);

    private void AssertUnchanged(TerminalGoalSweepOwnedRootResult result, string[][] snapshots)
    {
        Assert.DoesNotContain(result.OperatorEvents, line => line.StartsWith("SWEEP_BUILD_ROOT_RETENTION", StringComparison.Ordinal));
        Assert.Empty(result.ReclaimedGoalRoots!);
        Assert.Equal(0, result.RemovedCount);
        for (var index = 0; index < _roots.Count; index++) Assert.Equal(snapshots[index], Snapshot(_roots[index]));
    }

    private static string Summary(TerminalGoalSweepOwnedRootResult result) =>
        Assert.Single(result.OperatorEvents.Where(line => line.StartsWith("SWEEP_BUILD_ROOT_RETENTION ", StringComparison.Ordinal)));

    private static string EnabledPolicy() =>
        (ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 3 }).ToJson();

    private static void InsertGoal(string db, string goalId, string status, DateTimeOffset updated)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, $status, 'retention fixture', $updated, '{}')";
        command.Parameters.AddWithValue("$id", goalId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", updated.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];

    public void Dispose()
    {
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, Storage).Count == 25) { }
        Directory.Delete(_temp, recursive: true);
    }
}
