using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each instance owns its storage root, repository paths and SQLite registry.
public sealed class UnstoredGoalBuildRootRetentionTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("unstored-retention-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow.AddDays(30);
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));
    private string ReclaimFolder => Path.Combine(Storage.RootPath, "goal-root-reclaim");

    [Fact]
    public void OldUnstoredRoots_AcrossLeaseShapes_ReclaimFourRootsAndBytes()
    {
        var first = CreateLeaseless("aaaa0001", 2938);
        var second = Path.Combine(Storage.RootPath, "goals", "aaaa0002");
        Directory.CreateDirectory(Path.Combine(second, "artifacts"));
        var third = CreateLeased("aaaa0003", Path.Combine(_temp, "absent-repo"));
        var repository = Path.Combine(_temp, "known-repo");
        var store = Path.Combine(repository, ".orchestrator", "state.db");
        StateDbMigrations.EnsureUpToDate(store);
        var fourth = CreateLeased("aaaa0004", repository);

        var result = Retention().Run([], [store], 7, 25, 1_073_741_824, null);

        Assert.Equal(4, result.InspectedCount);
        Assert.Equal(4, result.ReclaimedCount);
        Assert.True(result.BytesReclaimed >= 3938);
        Assert.Equal(new[] { first, second, third, fourth }, result.ReclaimedRoots);
        Assert.All(result.ReclaimedRoots, root => Assert.False(Directory.Exists(root)));
        Assert.StartsWith("SWEEP_UNSTORED_BUILD_ROOT_RETENTION window-days=7 inspected=4 reclaimed=4", result.SummaryLine);
        Assert.Empty(Directory.GetDirectories(ReclaimFolder));
    }

    [Theory]
    [InlineData("manual", UnstoredGoalBuildRootKeepReason.NotGoalName)]
    [InlineData("stored", UnstoredGoalBuildRootKeepReason.GoalStored)]
    [InlineData("registered", UnstoredGoalBuildRootKeepReason.RegisteredOwnedRoot)]
    [InlineData("missing-lease", UnstoredGoalBuildRootKeepReason.LeaseUnreadable)]
    [InlineData("wrong-prefix", UnstoredGoalBuildRootKeepReason.LeaseUnreadable)]
    [InlineData("missing-repository", UnstoredGoalBuildRootKeepReason.LeaseUnreadable)]
    [InlineData("recent-lease", UnstoredGoalBuildRootKeepReason.WithinWindow)]
    [InlineData("live-owner", UnstoredGoalBuildRootKeepReason.OwnerLive)]
    [InlineData("foreign-store", UnstoredGoalBuildRootKeepReason.ForeignStore)]
    [InlineData("recent-payload", UnstoredGoalBuildRootKeepReason.WithinWindow)]
    public void AmbiguousRoot_FirstMatchingRule_KeepsSnapshot(string shape, object expectedReason)
    {
        var leased = shape is "missing-lease" or "wrong-prefix" or "missing-repository" or
            "recent-lease" or "live-owner" or "foreign-store";
        var repository = Path.Combine(_temp, "repo");
        var root = leased ? CreateLeased("aaaa0001", repository)
            : CreateLeaseless(shape == "manual" ? "manual" : "aaaa0001", 2938);
        var registry = Registry();
        if (shape == "registered")
            registry.Register(root, OwnedRunRootPurpose.RunAttempt,
                new SpawnProcessIdentity(999999, _now.AddDays(-30), Path.Combine(_temp, "dotnet.exe")),
                "aaaa0001000000000000000000000000", null, _now.AddDays(-30));
        if (shape == "missing-lease") File.Delete(Path.Combine(root, "lease", "lease.json"));
        if (shape == "wrong-prefix") EditLease(root, "goalId", JsonValue.Create("ffff0001000000000000000000000000"));
        if (shape == "missing-repository") EditLease(root, "repositoryRoot", null);
        if (shape == "recent-lease") EditLease(root, "lastUsedAt", JsonValue.Create(_now.AddDays(-1)));
        if (shape == "foreign-store") StateDbMigrations.EnsureUpToDate(Path.Combine(repository, ".orchestrator", "state.db"));
        if (shape == "recent-payload") File.SetLastWriteTimeUtc(Payload(root), _now.AddDays(-1).UtcDateTime);
        var before = Snapshot(root);
        string[] stored = shape == "stored" ? ["AAAA0001000000000000000000000000"] : [];

        var result = new UnstoredGoalBuildRootRetention(Storage, registry, _ => shape == "live-owner", new FixedTimeProvider(_now))
            .Run(stored, [], 7, 25, 1_073_741_824, null);

        Assert.Equal(before, Snapshot(root));
        Assert.Equal(0, result.ReclaimedCount);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Empty(result.ReclaimedRoots);
        var kept = Assert.Single(result.Kept);
        Assert.Equal((UnstoredGoalBuildRootKeepReason)expectedReason, kept.Key);
        Assert.Equal(1, kept.Value);
    }

    [Fact]
    public void DisabledRetention_TouchesNothingAndReturnsEmptySummary()
    {
        var root = CreateLeaseless("aaaa0001", 2938);
        var before = Snapshot(root);

        var result = Retention().Run([], [], 0, 25, 1_073_741_824, null);

        Assert.Equal(before, Snapshot(root));
        Assert.Empty(result.SummaryLine);
        Assert.Empty(result.Kept);
        Assert.Empty(result.ReclaimedRoots);
        Assert.Equal(0, result.InspectedCount);
        Assert.Equal(0, result.ReclaimedCount);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Null(result.NextCursor);
        Assert.False(Directory.Exists(ReclaimFolder));
    }

    [Fact]
    public void InspectionBudget_ContinuesAfterCursorAndWrapsOnce()
    {
        var roots = Enumerable.Range(1, 30).Select(index => CreateLeaseless($"bbbb{index:0000}", 10)).ToArray();
        File.SetLastWriteTimeUtc(Payload(roots[0]), _now.AddDays(-1).UtcDateTime);
        var before = Snapshot(roots[0]);
        var retention = Retention();

        var first = retention.Run([], [], 7, 25, 1_073_741_824, null);

        Assert.Equal(25, first.InspectedCount);
        Assert.Equal(24, first.ReclaimedCount);
        Assert.Equal(1, first.Kept[UnstoredGoalBuildRootKeepReason.WithinWindow]);
        Assert.Equal(5, first.Kept[UnstoredGoalBuildRootKeepReason.InspectionDeferred]);
        Assert.Equal(2, first.Kept.Count);
        Assert.Equal("bbbb0025", first.NextCursor);
        Assert.Equal(roots.Skip(1).Take(24), first.ReclaimedRoots);
        Assert.All(roots.Skip(25), root => Assert.True(Directory.Exists(root)));

        var second = retention.Run([], [], 7, 25, 1_073_741_824, first.NextCursor);

        Assert.Equal(6, second.InspectedCount);
        Assert.Equal(5, second.ReclaimedCount);
        Assert.Equal(roots.Skip(25), second.ReclaimedRoots);
        Assert.All(roots.Skip(1), root => Assert.False(Directory.Exists(root)));
        var kept = Assert.Single(second.Kept);
        Assert.Equal(UnstoredGoalBuildRootKeepReason.WithinWindow, kept.Key);
        Assert.Equal(1, kept.Value);
        Assert.Equal("bbbb0001", second.NextCursor);
        Assert.Equal(before, Snapshot(roots[0]));
    }

    [Fact]
    public void ByteBudget_AllowsFirstRootThenKeepsNextUnchanged()
    {
        var first = CreateLeaseless("bbbb0001", 600);
        var second = CreateLeaseless("bbbb0002", 600);
        var before = Snapshot(second);

        var result = Retention().Run([], [], 7, 25, 500, null);

        Assert.Equal(2, result.InspectedCount);
        Assert.Equal(1, result.ReclaimedCount);
        Assert.Equal(600, result.BytesReclaimed);
        Assert.Equal(first, Assert.Single(result.ReclaimedRoots));
        Assert.False(Directory.Exists(first));
        Assert.Equal(before, Snapshot(second));
        var kept = Assert.Single(result.Kept);
        Assert.Equal(UnstoredGoalBuildRootKeepReason.BudgetExhausted, kept.Key);
        Assert.Equal(1, kept.Value);
    }

    private string CreateLeaseless(string name, int bytes)
    {
        var root = Path.Combine(Storage.RootPath, "goals", name);
        Directory.CreateDirectory(Path.GetDirectoryName(Payload(root))!);
        File.WriteAllBytes(Payload(root), new byte[bytes]);
        return root;
    }

    private string CreateLeased(string prefix, string repository)
    {
        var root = DotnetBuildEnvironmentManager.CreateAttempt(new GoalId(prefix + new string('0', 24)),
            "acceptance", storageRoot: Storage, repositoryRoot: repository).RootPath;
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[1000]);
        return root;
    }

    private static string Payload(string root) => Path.Combine(root, "artifacts", "completion-build-evidence",
        "00000000000000000000000000000000.result.json");

    private static void EditLease(string root, string key, JsonNode? value)
    {
        var path = Path.Combine(root, "lease", "lease.json");
        var lease = JsonNode.Parse(File.ReadAllText(path))!;
        lease[key] = value;
        File.WriteAllText(path, lease.ToJsonString());
    }

    private OwnedRunRootRegistry Registry()
    {
        var db = Path.Combine(_temp, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        return new OwnedRunRootRegistry(db);
    }

    private UnstoredGoalBuildRootRetention Retention() => new(Storage, Registry(), _ => false, new FixedTimeProvider(_now));

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
