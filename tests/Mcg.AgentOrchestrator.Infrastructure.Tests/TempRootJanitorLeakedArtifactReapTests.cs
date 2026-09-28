using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TempRootJanitorLeakedArtifactReapTests
{
    [Fact]
    public void ReapsOnlyOldUnheldExactNamesFromLegacyAndPurposeLocations()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"artifact-reap-{Guid.NewGuid():N}");
        var parent = Path.Combine(temp, "mcg-run", "tmp");
        var captures = Path.Combine(parent, "acceptance-capture");
        var digests = Path.Combine(parent, "worktree-digest");
        Directory.CreateDirectory(captures);
        Directory.CreateDirectory(digests);
        var now = DateTimeOffset.UtcNow.AddDays(2);
        var old = now.AddHours(-3).UtcDateTime;
        var fresh = now.AddMinutes(-15).UtcDateTime;
        var oldLegacyCapture = SeedFile(temp, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".out", "abcd", old);
        var oldNewCapture = SeedFile(captures, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".err", "12345", old);
        var oldParentCapture = SeedFile(parent, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".out", "abc", old);
        var freshCapture = SeedFile(captures, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".out", "fresh", fresh);
        var freshLegacyCapture = SeedFile(temp, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".out", "fresh", fresh);
        var heldCapture = SeedFile(temp, "mcg-acc-" + Guid.NewGuid().ToString("N") + ".err", "held", old);
        var unmatchedCapture = SeedFile(temp, "mcg-acc-test-" + Guid.NewGuid().ToString("N") + ".out", "other", old);
        var oldLegacyDigest = SeedFolder(temp, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "123456", old);
        var oldNewDigest = SeedFolder(digests, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "1234567", old);
        var oldParentDigest = SeedFolder(parent, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "xy", old);
        var freshDigest = SeedFolder(digests, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "fresh", fresh);
        var freshLegacyDigest = SeedFolder(temp, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "fresh", fresh);
        var heldDigest = SeedFolder(temp, "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"), "held", old);
        var unmatchedDigest = SeedFolder(temp, "mcg-worktree-digest-x", "other", old);
        try
        {
            using var captureLease = File.Open(heldCapture, FileMode.Open, FileAccess.Read, FileShare.None);
            using var digestLease = File.Open(Path.Combine(heldDigest, "content"), FileMode.Open, FileAccess.Read, FileShare.None);
            var result = TempRootJanitor.ReapLeakedTempArtifacts(temp, parent, new FixedClock(now));
            Assert.Equal(3, result.RemovedFiles);
            Assert.Equal(3, result.RemovedFolders);
            Assert.Equal(27, result.BytesReclaimed);
            Assert.Equal(4, result.RetainedFresh);
            Assert.Equal(2, result.RetainedHeld);
            Assert.Equal(0, result.Failed);
            Assert.Equal("temp-artifact-janitor removed_files=3 removed_folders=3 bytes=27 retained_fresh=4 retained_held=2 failed=0", result.SummaryLine);
            Assert.False(File.Exists(oldLegacyCapture));
            Assert.False(File.Exists(oldNewCapture));
            Assert.False(File.Exists(oldParentCapture));
            Assert.False(Directory.Exists(oldLegacyDigest));
            Assert.False(Directory.Exists(oldNewDigest));
            Assert.False(Directory.Exists(oldParentDigest));
            Assert.True(File.Exists(freshCapture));
            Assert.True(File.Exists(freshLegacyCapture));
            Assert.True(Directory.Exists(freshDigest));
            Assert.True(Directory.Exists(freshLegacyDigest));
            Assert.True(File.Exists(unmatchedCapture));
            Assert.True(Directory.Exists(unmatchedDigest));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static string SeedFile(string parent, string name, string contents, DateTime timestamp)
    {
        var path = Path.Combine(parent, name);
        File.WriteAllText(path, contents);
        File.SetCreationTimeUtc(path, timestamp);
        File.SetLastWriteTimeUtc(path, timestamp);
        return path;
    }

    private static string SeedFolder(string parent, string name, string contents, DateTime timestamp)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        SeedFile(path, "content", contents, timestamp);
        Directory.SetCreationTimeUtc(path, timestamp);
        Directory.SetLastWriteTimeUtc(path, timestamp);
        return path;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
