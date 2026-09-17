using System.Globalization;
using System.Text;

public sealed class AssemblyTempRootOwnershipTests
{
    [Fact]
    public void OwnedRootAndLeaseMatchInfrastructureOnDiskContract()
    {
        var sharedRoot = Path.Combine("root", "mcg-tests");

        var ownedRoot = AssemblyTempRootOwnership.BuildProcessTempRoot(sharedRoot, 0x1a2b);

        Assert.Equal(Path.Combine(sharedRoot, "p1a2b"), ownedRoot);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(sharedRoot), ".p1a2b.owner.lock"),
            AssemblyTempRootOwnership.RootLeasePath(ownedRoot));
    }

    [Theory]
    [InlineData("p1", 1)]
    [InlineData("p1a2b", 0x1a2b)]
    public void OwnedRootNameUsesPositiveHexProcessId(string name, int expectedProcessId)
    {
        Assert.True(AssemblyTempRootOwnership.TryParseProcessTempRootName(name, out var processId));
        Assert.Equal(expectedProcessId, processId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p0")]
    [InlineData("p-1")]
    [InlineData("legacy")]
    [InlineData("pnot-hex")]
    public void UnownedRootNamesAreRejected(string? name)
    {
        Assert.False(AssemblyTempRootOwnership.TryParseProcessTempRootName(name, out _));
    }

    [Fact]
    public void ReapSelectionPreservesLiveReusedInaccessibleAndAmbiguousIdentities()
    {
        var sharedRoot = Path.GetFullPath("fixture");
        var recorded = DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00", CultureInfo.InvariantCulture);
        var identities = new Dictionary<int, CoreTempRootIdentity?>
        {
            [1] = new(1, recorded, "one"),
            [2] = new(2, recorded, "two"),
            [3] = new(3, recorded, "three"),
            [4] = null,
            [5] = new(5, recorded, "five")
        };

        var candidates = AssemblyTempRootOwnership.SelectReapableRoots(
            sharedRoot,
            ["p1", "p2", "p3", "p4", "p5", "legacy"],
            currentProcessId: 99,
            (_, processId) => identities[processId],
            processId => processId switch
            {
                1 => new(CoreProcessIdentityStatus.Exited, null),
                2 => new(CoreProcessIdentityStatus.Running, recorded),
                3 => new(CoreProcessIdentityStatus.Inaccessible, null),
                4 => new(CoreProcessIdentityStatus.Exited, null),
                5 => new(CoreProcessIdentityStatus.Running, recorded.AddMinutes(1)),
                _ => throw new InvalidOperationException()
            });

        Assert.Equal(["p1", "p5"], candidates.Select(candidate => candidate.Name));
    }

    [Fact]
    public void BoundedSelectionReapsAtMostThirtyTwoOldestRoots()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var candidates = Enumerable.Range(1, 40)
            .Select(processId => new CoreReapCandidate(
                $"p{processId:x}",
                processId,
                new CoreTempRootIdentity(processId, startedAt, "test")))
            .ToArray();

        var bounded = AssemblyTempRootOwnership.SelectBoundedReapRoots(
            candidates,
            name => DateTime.UnixEpoch.AddSeconds(
                AssemblyTempRootOwnership.TryParseProcessTempRootName(name, out var processId) ? processId : 0),
            AssemblyTempRootOwnership.MaxRootsReapedPerProcess);

        Assert.Equal(32, bounded.Count);
        Assert.Equal(Enumerable.Range(1, 32), bounded.Select(candidate => candidate.ProcessId));
    }

    [Fact]
    public void RevalidationPreservesChangedIdentityAndContinuesPastLockedRoot()
    {
        using var fixture = new TempRootFixture("revalidation");
        var oldStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var changedStartedAt = oldStartedAt.AddMinutes(1);
        var lockedRoot = fixture.CreateOwnedRoot(101, oldStartedAt);
        var changedRoot = fixture.CreateOwnedRoot(102, changedStartedAt);
        using var lockedLease = new FileStream(
            AssemblyTempRootOwnership.RootLeasePath(lockedRoot),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var candidates = new[]
        {
            new CoreReapCandidate("p65", 101, new CoreTempRootIdentity(101, oldStartedAt, "test")),
            new CoreReapCandidate("p66", 102, new CoreTempRootIdentity(102, oldStartedAt, "test"))
        };

        var outcomes = AssemblyTempRootOwnership.ReapBoundedRoots(
            fixture.Root,
            candidates,
            _ => new(CoreProcessIdentityStatus.Exited, null),
            AssemblyTempRootOwnership.TryAcquireDeletionLease,
            AssemblyTempRootOwnership.DeleteTreeWithRetry);

        Assert.All(outcomes, outcome => Assert.Equal(CoreTempRootDeleteStatus.Preserved, outcome.Status));
        Assert.True(Directory.Exists(lockedRoot));
        Assert.True(Directory.Exists(changedRoot));
    }

    [Fact]
    public void ReaperDeletesDeadRootUnderTestTmpAndLeavesSiblingLiveRoot()
    {
        using var fixture = new TempRootFixture(".test-tmp");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var deadRoot = fixture.CreateOwnedRoot(201, startedAt);
        var liveRoot = fixture.CreateOwnedRoot(202, startedAt);
        using var liveLease = new FileStream(
            AssemblyTempRootOwnership.RootLeasePath(liveRoot),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var candidates = new[]
        {
            new CoreReapCandidate("pc9", 201, new CoreTempRootIdentity(201, startedAt, "test")),
            new CoreReapCandidate("pca", 202, new CoreTempRootIdentity(202, startedAt, "test"))
        };

        var outcomes = AssemblyTempRootOwnership.ReapBoundedRoots(
            fixture.Root,
            candidates,
            _ => new(CoreProcessIdentityStatus.Exited, null),
            AssemblyTempRootOwnership.TryAcquireDeletionLease,
            AssemblyTempRootOwnership.DeleteTreeWithRetry);

        Assert.Equal(CoreTempRootDeleteStatus.Deleted, outcomes[0].Status);
        Assert.Equal(CoreTempRootDeleteStatus.Preserved, outcomes[1].Status);
        Assert.False(Directory.Exists(deadRoot));
        Assert.True(Directory.Exists(liveRoot));
    }

    [Fact]
    public void OwnedRootReleaseDeletesOnlyCurrentRootAndItsLease()
    {
        using var fixture = new TempRootFixture("release");
        var currentRoot = AssemblyTempRootOwnership.BuildProcessTempRoot(fixture.Root, Environment.ProcessId);
        var siblingRoot = Path.Combine(fixture.Root, "p7ffffffe");
        Directory.CreateDirectory(currentRoot);
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Combine(currentRoot, "owned.txt"), "owned");
        File.WriteAllText(Path.Combine(siblingRoot, "sibling.txt"), "sibling");
        using var owner = AssemblyTempRootOwnership.TryAcquireOwnedRoot(currentRoot);
        Assert.NotNull(owner);

        var outcome = owner.Release();

        Assert.Equal(CoreTempRootDeleteStatus.Deleted, outcome.Status);
        Assert.False(Directory.Exists(currentRoot));
        Assert.False(File.Exists(AssemblyTempRootOwnership.RootLeasePath(currentRoot)));
        Assert.True(Directory.Exists(siblingRoot));
    }

    [Fact]
    public void ContainmentRejectsTraversalAndMismatchedProcessId()
    {
        var sharedRoot = Path.GetFullPath(Path.Combine("fixture", "mcg-tests"));

        Assert.True(AssemblyTempRootOwnership.IsContainedOwnedRoot(sharedRoot, Path.Combine(sharedRoot, "p2a"), 0x2a));
        Assert.False(AssemblyTempRootOwnership.IsContainedOwnedRoot(sharedRoot, Path.Combine(sharedRoot, "..", "p2a"), 0x2a));
        Assert.False(AssemblyTempRootOwnership.IsContainedOwnedRoot(sharedRoot, Path.Combine(sharedRoot, "p2a"), 0x2b));
    }

    private sealed class TempRootFixture : IDisposable
    {
        internal TempRootFixture(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), $"mcg-core-temp-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string CreateOwnedRoot(int processId, DateTimeOffset startedAt)
        {
            var root = AssemblyTempRootOwnership.BuildProcessTempRoot(Root, processId);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "payload.txt"), "payload");
            File.WriteAllText(
                AssemblyTempRootOwnership.RootLeasePath(root),
                $"pid={processId};startedAt={startedAt:O};path=test",
                Encoding.UTF8);
            return root;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
