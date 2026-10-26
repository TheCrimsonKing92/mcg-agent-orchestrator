using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every filesystem root and SQLite registry belongs to this test instance.
public sealed class TerminalBuildRootRetentionTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("terminal-retention-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));
    private string ReclaimFolder => Path.Combine(Storage.RootPath, "goal-root-reclaim");

    [Fact]
    public void OldUnreferencedTerminalRootsAreReclaimedWithinBudget()
    {
        var roots = CreateThree();
        var retention = Retention();
        var first = retention.Run(roots.Select(pair => pair.Fact).ToArray(), 3, 2);
        Assert.Equal(2, first.ReclaimedCount);
        Assert.True(first.BytesReclaimed >= 2000);
        Assert.Equal(1, first.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
        Assert.StartsWith("SWEEP_BUILD_ROOT_RETENTION window-days=3 reclaimed=2", first.SummaryLine);
        Assert.Equal(roots.Take(2).Select(pair => pair.Root), first.ReclaimedRoots);
        Assert.False(Directory.Exists(roots[0].Root));
        Assert.False(Directory.Exists(roots[1].Root));
        Assert.True(Directory.Exists(roots[2].Root));
        Assert.Empty(Directory.GetDirectories(ReclaimFolder));
        var second = retention.Run(roots.Select(pair => pair.Fact).ToArray(), 3, 2);
        Assert.Equal(1, second.ReclaimedCount);
        Assert.Equal(roots[2].Root, Assert.Single(second.ReclaimedRoots));
        Assert.False(Directory.Exists(roots[2].Root));
        Assert.Empty(Directory.GetDirectories(ReclaimFolder));
    }

    [Theory]
    [InlineData("Active", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Parked", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("WaitingForHuman", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Verifying", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Verified", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("AcceptanceFailed", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Failed", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Draft", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("not-stored", TerminalBuildRootSkipReason.GoalNotStored)]
    [InlineData("ambiguous", TerminalBuildRootSkipReason.GoalPrefixAmbiguous)]
    [InlineData("missing-lease", TerminalBuildRootSkipReason.LeaseUnreadable)]
    [InlineData("wrong-id", TerminalBuildRootSkipReason.LeaseUnreadable)]
    [InlineData("registered", TerminalBuildRootSkipReason.RegisteredOwnedRoot)]
    [InlineData("recent", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("live", TerminalBuildRootSkipReason.OwnerLive)]
    [InlineData("missing-updated", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("recent-updated", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("missing-last-used", TerminalBuildRootSkipReason.LeaseUnreadable)]
    public void NonTerminalAndReferencedRootsAreNeverTouched(string shape, object expectedReason)
    {
        // object keeps the public test signature independent of the internal production enum.
        var reason = (TerminalBuildRootSkipReason)expectedReason;
        var goal = new GoalId("11111111000000000000000000000000");
        var root = CreateRoot(goal);
        var facts = new List<StoredGoalRetentionFact>
        { new(goal.Value, Enum.TryParse<GoalStatus>(shape, out var status) ? status : GoalStatus.Completed, _now.AddDays(-10)) };
        var registry = Registry();
        if (shape == "not-stored") facts.Clear();
        if (shape == "ambiguous") facts.Add(new("11111111ffffffffffffffffffffffff", GoalStatus.Parked, _now.AddDays(-10)));
        if (shape == "missing-lease") File.Delete(Path.Combine(root, "lease", "lease.json"));
        if (shape == "wrong-id") EditLease(root, "goalId", JsonValue.Create("11111111ffffffffffffffffffffffff"));
        if (shape == "recent") EditLease(root, "lastUsedAt", JsonValue.Create(_now.AddHours(-12)));
        if (shape == "missing-last-used") EditLease(root, "lastUsedAt", null);
        if (shape == "missing-updated") facts[0] = facts[0] with { UpdatedAt = null };
        if (shape == "recent-updated") facts[0] = facts[0] with { UpdatedAt = _now.AddHours(-12) };
        if (shape == "registered")
            registry.Register(root, OwnedRunRootPurpose.RunAttempt,
                new SpawnProcessIdentity(999999, _now.AddDays(-10), Path.Combine(_temp, "dotnet.exe")),
                goal.Value, null, _now.AddDays(-10));
        var before = Snapshot(root);
        var result = new TerminalBuildRootRetention(Storage, registry, _ => shape == "live", TimeProvider.System)
            .Run(facts, 1, 10);
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(0, result.ReclaimedCount);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(reason, skip.Key);
        Assert.Equal(1, skip.Value);
    }

    [Fact]
    public void HeldRootIsSkippedReportedAndReclaimedOnceReleased()
    {
        var root = CreateRoot(new GoalId("11111111000000000000000000000000"));
        var facts = new[] { new StoredGoalRetentionFact("11111111000000000000000000000000", GoalStatus.Completed, _now.AddDays(-10)) };
        var before = Snapshot(root);
        var retention = Retention();
        using (var held = new FileStream(Path.Combine(root, "payload.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = retention.Run(facts, 1, 2);
            Assert.Equal(0, first.ReclaimedCount);
            Assert.True(Directory.Exists(root));
            Assert.Equal(1, first.Skipped[TerminalBuildRootSkipReason.Held]);
            Assert.Contains("held:1", first.SummaryLine);
        }
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(1, retention.Run(facts, 1, 2).ReclaimedCount);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ZeroDaysTouchesNothing()
    {
        var roots = CreateThree();
        var snapshots = roots.Select(pair => Snapshot(pair.Root)).ToArray();
        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 0, 2);
        Assert.Equal(0, result.ReclaimedCount);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.ReclaimedRoots);
        Assert.Empty(result.SummaryLine);
        Assert.False(Directory.Exists(ReclaimFolder));
        for (var index = 0; index < roots.Length; index++)
            Assert.Equal(snapshots[index], Snapshot(roots[index].Root));
    }

    [Fact]
    public void FailedRemovalIsRetriedBeforeNewRootsAndConsumesBudget()
    {
        var roots = CreateThree();
        var readOnly = Path.Combine(roots[0].Root, "payload.bin");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string? renamedFile = null;
        try
        {
            var retention = Retention();
            var facts = roots.Select(pair => pair.Fact).ToArray();
            var failed = retention.Run(facts, 1, 1);
            Assert.Equal(0, failed.ReclaimedCount);
            Assert.Equal(0, failed.BytesReclaimed);
            Assert.Equal(1, failed.Skipped[TerminalBuildRootSkipReason.RemovalFailed]);
            Assert.Equal(2, failed.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
            Assert.False(Directory.Exists(roots[0].Root));
            var renamed = Assert.Single(Directory.GetDirectories(ReclaimFolder));
            renamedFile = Path.Combine(renamed, "payload.bin");
            Assert.True(File.Exists(renamedFile));
            File.SetAttributes(renamedFile, FileAttributes.Normal);
            var retry = retention.Run(facts, 1, 1);
            Assert.Equal(1, retry.ReclaimedCount);
            Assert.True(retry.BytesReclaimed >= 1000);
            Assert.Equal(renamed, Assert.Single(retry.ReclaimedRoots));
            Assert.Equal(2, retry.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
            Assert.All(roots.Skip(1), pair => Assert.True(Directory.Exists(pair.Root)));
            Assert.Empty(Directory.GetDirectories(ReclaimFolder));
        }
        finally
        {
            if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal);
            if (renamedFile is not null && File.Exists(renamedFile)) File.SetAttributes(renamedFile, FileAttributes.Normal);
        }
    }

    private (StoredGoalRetentionFact Fact, string Root)[] CreateThree() =>
        new[] { GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Superseded }
            .Select((status, index) =>
            {
                var goal = new GoalId($"{index + 1:00000000}000000000000000000000000");
                return (new StoredGoalRetentionFact(goal.Value, status, _now.AddDays(-10)), CreateRoot(goal));
            }).ToArray();

    private string CreateRoot(GoalId goal)
    {
        var root = DotnetBuildEnvironmentManager.CreateAttempt(goal, "acceptance",
            storageRoot: Storage, repositoryRoot: Path.Combine(_temp, "repo")).RootPath;
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[1000]);
        EditLease(root, "lastUsedAt", JsonValue.Create(_now.AddDays(-10)));
        return root;
    }

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

    private TerminalBuildRootRetention Retention() => new(Storage, Registry(), _ => false, TimeProvider.System);

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
