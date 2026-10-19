using Mcg.AgentOrchestrator.Infrastructure;

// Mutates the process environment only in the serial build-slot collection, using private temporary roots.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsSharedIsolatedRoot
{
    [Xunit.Fact]
    public void FixturesShareOneOwnerUntilTheLastSingleRelease()
    {
        var basePath = CreateBasePath();
        var original = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            var calls = 0;
            var shared = new SharedIsolatedDotnetRoot(() =>
            {
                calls++;
                return new IsolatedDotnetRootFixture(basePath, null);
            });
            using var first = new IsolatedDotnetRootFixture(shared);
            using var second = new IsolatedDotnetRootFixture(shared);
            var root = first.RootPath;
            var ownerLock = root + IsolatedDotnetRootFixture.OwnerLockSuffix;
            Xunit.Assert.Equal(root, second.RootPath);
            Xunit.Assert.Equal(1, calls);
            Xunit.Assert.Equal(2, shared.Holders);
            Xunit.Assert.Equal(root, ReadOverride());
            Xunit.Assert.True(Directory.Exists(root));
            Xunit.Assert.True(File.Exists(ownerLock));

            first.Dispose();
            Xunit.Assert.Equal(root, ReadOverride());
            Xunit.Assert.True(Directory.Exists(root));
            Xunit.Assert.True(File.Exists(ownerLock));
            Xunit.Assert.Equal(1, shared.Holders);
            first.Dispose();
            Xunit.Assert.Equal(1, shared.Holders);
            Xunit.Assert.Equal(root, ReadOverride());

            second.Dispose();
            Xunit.Assert.Equal(original, ReadOverride());
            Xunit.Assert.False(Directory.Exists(root));
            Xunit.Assert.False(File.Exists(ownerLock));
            Xunit.Assert.Equal(0, shared.Holders);
            second.Dispose();
            Xunit.Assert.Equal(0, shared.Holders);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, original);
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailedFactoryLeavesNoHolderAndTheNextAcquireRetries()
    {
        var basePath = CreateBasePath();
        var original = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            var calls = 0;
            var shared = new SharedIsolatedDotnetRoot(() =>
            {
                if (++calls == 1)
                    throw new InvalidOperationException("factory failure");
                return new IsolatedDotnetRootFixture(basePath, null);
            });
            Xunit.Assert.Throws<InvalidOperationException>(() => shared.Acquire());
            Xunit.Assert.Equal(1, calls);
            Xunit.Assert.Equal(0, shared.Holders);
            Xunit.Assert.Equal(original, ReadOverride());
            using (var fixture = new IsolatedDotnetRootFixture(shared))
            {
                Xunit.Assert.Equal(2, calls);
                Xunit.Assert.Equal(1, shared.Holders);
                Xunit.Assert.Equal(fixture.RootPath, ReadOverride());
                Xunit.Assert.True(Directory.Exists(fixture.RootPath));
            }
            Xunit.Assert.Equal(0, shared.Holders);
            Xunit.Assert.Equal(original, ReadOverride());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, original);
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void AcquireAfterReleaseToZeroCreatesAFreshOwner()
    {
        var basePath = CreateBasePath();
        var original = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            var calls = 0;
            var shared = new SharedIsolatedDotnetRoot(() =>
            {
                calls++;
                return new IsolatedDotnetRootFixture(basePath, null);
            });
            var firstRoot = shared.Acquire();
            try
            {
                Xunit.Assert.Equal(1, calls);
                Xunit.Assert.Equal(1, shared.Holders);
                Xunit.Assert.Equal(firstRoot, ReadOverride());
            }
            finally
            {
                shared.Release();
            }
            Xunit.Assert.Equal(0, shared.Holders);
            Xunit.Assert.Equal(original, ReadOverride());
            Xunit.Assert.False(Directory.Exists(firstRoot));

            var secondRoot = shared.Acquire();
            try
            {
                Xunit.Assert.NotEqual(firstRoot, secondRoot);
                Xunit.Assert.Equal(2, calls);
                Xunit.Assert.Equal(1, shared.Holders);
                Xunit.Assert.Equal(secondRoot, ReadOverride());
                Xunit.Assert.True(Directory.Exists(secondRoot));
            }
            finally
            {
                shared.Release();
            }
            Xunit.Assert.Equal(0, shared.Holders);
            Xunit.Assert.Equal(original, ReadOverride());
            Xunit.Assert.False(Directory.Exists(secondRoot));
            Xunit.Assert.False(File.Exists(secondRoot + IsolatedDotnetRootFixture.OwnerLockSuffix));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, original);
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Xunit.Fact]
    public void OneHolderPreservesTheDirectFixtureLifecycle()
    {
        var sharedBase = CreateBasePath();
        var directBase = CreateBasePath();
        var original = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            var held = ObserveLifecycle(sharedBase, original, useShared: true);
            var direct = ObserveLifecycle(directBase, original, useShared: false);
            Xunit.Assert.Equal(direct, held);
            Xunit.Assert.Equal(new LifecycleObservations(true, true, true, true), held);
            Xunit.Assert.Equal(new LifecycleObservations(true, true, true, true), direct);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, original);
            Directory.Delete(sharedBase, recursive: true);
            Directory.Delete(directBase, recursive: true);
        }
    }

    private static LifecycleObservations ObserveLifecycle(string basePath, string? original, bool useShared)
    {
        var shared = useShared ? new SharedIsolatedDotnetRoot(() => new IsolatedDotnetRootFixture(basePath, null)) : null;
        IsolatedDotnetRootFixture? direct = null;
        var root = shared is not null ? shared.Acquire() : (direct = new IsolatedDotnetRootFixture(basePath, null)).RootPath;
        bool parentMatches;
        try
        {
            var heldValue = ReadOverride();
            Xunit.Assert.Equal(root, heldValue);
            Xunit.Assert.True(Directory.Exists(root));
            Xunit.Assert.True(File.Exists(root + IsolatedDotnetRootFixture.OwnerLockSuffix));
            parentMatches = string.Equals(Path.GetDirectoryName(Path.GetFullPath(heldValue!)),
                Path.GetFullPath(basePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        finally
        {
            if (shared is not null)
                shared.Release();
            else
                direct!.Dispose();
        }
        return new LifecycleObservations(parentMatches, ReadOverride() == original,
            !Directory.Exists(root), !File.Exists(root + IsolatedDotnetRootFixture.OwnerLockSuffix));
    }

    private static string CreateBasePath() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;

    private static string? ReadOverride() =>
        Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);

    private sealed record LifecycleObservations(bool HeldParentMatchesBase, bool OriginalRestored, bool RootAbsent, bool OwnerLockAbsent);
}
