using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the canonical repository, project registry, stores and build roots are instance-owned.
public sealed class UnstoredGoalBuildRootRetentionScenarioTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("unstored-retention-scenario-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddDays(30);
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));
    private string Repo => Path.Combine(_temp, "repo");
    private string Db => Path.Combine(Repo, ".orchestrator", "state.db");

    [Fact]
    public void CanonicalSweep_ReclaimsLeaksAndFailsClosedWhenStoreIsUnreadable()
    {
        StateDbMigrations.EnsureUpToDate(Db);
        var projectRoot = Path.Combine(_temp, "other-repo");
        Directory.CreateDirectory(projectRoot);
        var projects = new OrchestratorProjectRegistry(Path.Combine(_temp, "registry"), Path.Combine(_temp, "data"));
        projects.CreateProject("other", projectRoot);
        var projectDb = projects.GetRequiredProject("other").ResolveWorkspace().SqliteStatePath;
        StateDbMigrations.EnsureUpToDate(projectDb);
        InsertGoal(projectDb, "aaaa0003000000000000000000000000", "Active");
        InsertGoal(Db, "aaaa0004000000000000000000000000", "Parked");
        var leaked = CreateLeaseless("aaaa0001");
        var leasedLeak = CreateLeased("aaaa0002", Path.Combine(_temp, "absent-repo"));
        var projectGoal = CreateLeased("aaaa0003", projectRoot);
        var parkedGoal = CreateLeased("aaaa0004", Repo);
        var recent = CreateLeaseless("aaaa0005");
        File.SetLastWriteTimeUtc(Payload(recent), _now.AddDays(-1).UtcDateTime);
        var manual = CreateLeaseless("manual");
        var protectedRoots = new[] { projectGoal, parkedGoal, recent, manual };
        var before = protectedRoots.Select(Snapshot).ToArray();
        var state = new TerminalGoalSweep.OwnedRootSweepState();

        TerminalGoalSweepOwnedRootResult Sweep() => TerminalGoalSweep.ReapOwnedBuildRootsCore(
            Db, Storage, state, _ => false, usesSharedStorageRoot: true,
            canonicalRepoRoot: Repo, projectRegistry: projects, reclaimGoalRoots: false,
            policy: ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 7 },
            timeProvider: new FixedTimeProvider(_now));

        var first = Sweep();

        Assert.False(Directory.Exists(leaked));
        Assert.False(Directory.Exists(leasedLeak));
        AssertUnchanged(protectedRoots, before);
        var events = first.OperatorEvents.ToList();
        var unstored = Assert.Single(events.Where(line => line.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION ", StringComparison.Ordinal)));
        var stored = Assert.Single(events.Where(line => line.StartsWith("SWEEP_BUILD_ROOT_RETENTION ", StringComparison.Ordinal)));
        Assert.Contains("reclaimed=2", unstored);
        Assert.Contains("goal-stored:2", unstored);
        Assert.Contains("within-window:1", unstored);
        Assert.Contains("not-goal-name:1", unstored);
        Assert.Contains("goal-not-terminal:2", stored);
        Assert.Equal(events.IndexOf(stored) + 1, events.IndexOf(unstored));

        var second = Sweep();

        Assert.Contains("reclaimed=0", Assert.Single(second.OperatorEvents.Where(line =>
            line.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION ", StringComparison.Ordinal))));
        AssertUnchanged(protectedRoots, before);

        var tenantDb = Path.Combine(Repo, ".orchestrator", "tenants", "unknown", "state.db");
        StateDbMigrations.EnsureUpToDate(tenantDb);
        InsertGoal(tenantDb, "ffffffff000000000000000000000000", "unknown-status");
        var third = Sweep();

        Assert.DoesNotContain(third.OperatorEvents, line => line.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION", StringComparison.Ordinal));
        Assert.Contains("reasons=goal-store-unavailable", Assert.Single(third.OperatorEvents.Where(line =>
            line.StartsWith("SWEEP_BUILD_ROOT_RETENTION ", StringComparison.Ordinal))));
        AssertUnchanged(protectedRoots, before);
    }

    private string CreateLeaseless(string name)
    {
        var root = Path.Combine(Storage.RootPath, "goals", name);
        Directory.CreateDirectory(Path.GetDirectoryName(Payload(root))!);
        File.WriteAllBytes(Payload(root), new byte[2938]);
        return root;
    }

    private string CreateLeased(string prefix, string repository) =>
        DotnetBuildEnvironmentManager.CreateAttempt(new GoalId(prefix + new string('0', 24)),
            "acceptance", storageRoot: Storage, repositoryRoot: repository).RootPath;

    private static string Payload(string root) => Path.Combine(root, "artifacts", "completion-build-evidence",
        "00000000000000000000000000000000.result.json");

    private void InsertGoal(string db, string goal, string status)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO goals (id, status, objective, updated_at, snapshot_json) VALUES ($id, $status, 'unstored retention fixture', $updated, '{}')";
        command.Parameters.AddWithValue("$id", goal);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", _now.AddDays(-30).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void AssertUnchanged(string[] roots, string[][] before)
    {
        for (var index = 0; index < roots.Length; index++) Assert.Equal(before[index], Snapshot(roots[index]));
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, Storage).Count == 25) { }
        Directory.Delete(_temp, recursive: true);
    }
}
