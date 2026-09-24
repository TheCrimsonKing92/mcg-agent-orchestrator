using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsIsolatedRootReclaim
{
    [Xunit.Fact]
    public void ConstructionReclaimsUnheldRootsAndPreservesHeldRoot()
    {
        var basePath = CreateBasePath();
        var original = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            var first = CreateRoot(basePath, "mdi-first");
            var second = CreateRoot(basePath, "mdi-second");
            var held = CreateRoot(basePath, "mdi-held");
            var unrelated = CreateRoot(basePath, "other-root");
            var firstLock = CreateLock(first);
            var secondLock = CreateLock(second);
            var heldLock = CreateLock(held);
            var heldFiles = SnapshotFiles(held);
            var heldLockBytes = File.ReadAllBytes(heldLock);
            var unrelatedFiles = SnapshotFiles(unrelated);

            using (var heldHandle = new FileStream(heldLock, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var fixture = new IsolatedDotnetRootFixture(basePath, null))
            {
                Xunit.Assert.False(Directory.Exists(first));
                Xunit.Assert.False(Directory.Exists(second));
                Xunit.Assert.False(File.Exists(firstLock));
                Xunit.Assert.False(File.Exists(secondLock));
                Xunit.Assert.True(Directory.Exists(held));
                Xunit.Assert.Equal(heldFiles, SnapshotFiles(held));
                heldHandle.Position = 0;
                var lockBytesAfter = new byte[heldLockBytes.Length];
                heldHandle.ReadExactly(lockBytesAfter);
                Xunit.Assert.Equal(heldLockBytes, lockBytesAfter);
                Xunit.Assert.Equal(unrelatedFiles, SnapshotFiles(unrelated));
                Xunit.Assert.Equal(
                    new[] { held, fixture.RootPath }.Order(StringComparer.Ordinal).ToArray(),
                    Directory.EnumerateDirectories(basePath, "mdi-*").Order(StringComparer.Ordinal).ToArray());
                Xunit.Assert.Throws<IOException>(() =>
                {
                    using var probe = new FileStream(
                        fixture.RootPath + IsolatedDotnetRootFixture.OwnerLockSuffix,
                        FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
                Xunit.Assert.Equal(
                    fixture.RootPath,
                    Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable));
            }

            Xunit.Assert.Equal(original,
                Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConstructionReclaimsOnlyStaleLocklessRootsUsingNewestNestedFile()
    {
        var basePath = CreateBasePath();
        try
        {
            var stale = CreateRoot(basePath, "mdi-stale");
            var fresh = CreateRoot(basePath, "mdi-fresh");
            var staleTime = DateTime.UtcNow - IsolatedDotnetRootFixture.LockLessRootStaleAge - TimeSpan.FromDays(1);
            foreach (var file in Directory.EnumerateFiles(stale, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, staleTime);
            }

            var freshTopFile = Path.Combine(fresh, "top.txt");
            File.SetLastWriteTimeUtc(freshTopFile, staleTime);
            var freshFiles = SnapshotFiles(fresh);

            using (var fixture = new IsolatedDotnetRootFixture(basePath, null))
            {
                Xunit.Assert.False(Directory.Exists(stale));
                Xunit.Assert.True(Directory.Exists(fresh));
                Xunit.Assert.Equal(freshFiles, SnapshotFiles(fresh));
            }
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConstructionRemovesOnlyUnheldOrphanedOwnerLocks()
    {
        var basePath = CreateBasePath();
        try
        {
            var orphan = Path.Combine(basePath, "mdi-orphan" + IsolatedDotnetRootFixture.OwnerLockSuffix);
            var held = Path.Combine(basePath, "mdi-held" + IsolatedDotnetRootFixture.OwnerLockSuffix);
            var unrelated = Path.Combine(basePath, "other-root" + IsolatedDotnetRootFixture.OwnerLockSuffix);
            File.WriteAllText(orphan, "orphan");
            File.WriteAllText(held, "held");
            File.WriteAllText(unrelated, "unrelated");

            using (var heldHandle = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var fixture = new IsolatedDotnetRootFixture(basePath, null))
            {
                Xunit.Assert.False(File.Exists(orphan));
                Xunit.Assert.Equal("held", File.ReadAllText(held));
                Xunit.Assert.Equal("unrelated", File.ReadAllText(unrelated));
            }
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void DisposeFailureReportsOneDiagnosticAndDoesNotFailTest()
    {
        var basePath = CreateBasePath();
        var diagnostics = new List<string>();
        try
        {
            var fixture = new IsolatedDotnetRootFixture(basePath, diagnostics.Add);
            var root = fixture.RootPath;
            using (var heldFile = new FileStream(
                       Path.Combine(root, "held.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                fixture.Dispose();
            }

            var diagnostic = Xunit.Assert.Single(diagnostics);
            Xunit.Assert.Contains("phase=dispose", diagnostic, StringComparison.Ordinal);
            Xunit.Assert.Contains($"root={root}", diagnostic, StringComparison.Ordinal);
            Xunit.Assert.True(
                diagnostic.Contains("exceptionType=IOException", StringComparison.Ordinal) ||
                diagnostic.Contains("exceptionType=UnauthorizedAccessException", StringComparison.Ordinal),
                diagnostic);
            fixture.Dispose();
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ReclaimFailureReportsOneDiagnosticAndConstructionContinues()
    {
        var basePath = CreateBasePath();
        var diagnostics = new List<string>();
        try
        {
            var blockedRoot = CreateRoot(basePath, "mdi-blocked");
            CreateLock(blockedRoot);
            using (var heldFile = new FileStream(
                       Path.Combine(blockedRoot, "top.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var fixture = new IsolatedDotnetRootFixture(basePath, diagnostics.Add))
            {
                Xunit.Assert.True(Directory.Exists(blockedRoot));
                Xunit.Assert.True(Directory.Exists(fixture.RootPath));
            }

            var diagnostic = Xunit.Assert.Single(diagnostics);
            Xunit.Assert.Contains("phase=reclaim", diagnostic, StringComparison.Ordinal);
            Xunit.Assert.Contains($"root={blockedRoot}", diagnostic, StringComparison.Ordinal);
            Xunit.Assert.True(
                diagnostic.Contains("exceptionType=IOException", StringComparison.Ordinal) ||
                diagnostic.Contains("exceptionType=UnauthorizedAccessException", StringComparison.Ordinal),
                diagnostic);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    private static string CreateBasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mdi-reclaim-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateRoot(string basePath, string name)
    {
        var path = Path.Combine(basePath, name);
        var nested = Path.Combine(path, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(path, "top.txt"), $"top:{name}");
        File.WriteAllText(Path.Combine(nested, "deep.txt"), $"deep:{name}");
        return path;
    }

    private static string CreateLock(string root)
    {
        var path = root + IsolatedDotnetRootFixture.OwnerLockSuffix;
        File.WriteAllText(path, $"owner:{Path.GetFileName(root)}");
        return path;
    }

    private static string[] SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"{Path.GetRelativePath(root, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
