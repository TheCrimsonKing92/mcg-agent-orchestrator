using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnedRunRootReaperTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Released_root_is_removed_even_when_owner_is_live()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Live);
        var path = fixture.Register("released", OwnedRunRootPurpose.RunAttempt);
        fixture.Registry.Release(path, OwnedRunRootReleaseOutcome.Failed, Start);

        var result = fixture.Reaper.Reap(0, 10);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal([path], fixture.FileSystem.DeletedPaths);
    }

    [Xunit.Fact]
    public void Dead_owner_root_is_removed_without_a_release_write()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Gone);
        var path = fixture.Register("dead", OwnedRunRootPurpose.RunAttempt);

        var result = fixture.Reaper.Reap(0, 10);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal([path], fixture.FileSystem.DeletedPaths);
    }

    [Xunit.Theory]
    [Xunit.InlineData((int)OwnedRunRootOwnerLiveness.Live)]
    [Xunit.InlineData((int)OwnedRunRootOwnerLiveness.Mismatched)]
    [Xunit.InlineData((int)OwnedRunRootOwnerLiveness.Unknown)]
    public void Root_is_retained_when_an_owner_may_still_be_live(int livenessValue)
    {
        var liveness = (OwnedRunRootOwnerLiveness)livenessValue;
        using var fixture = new ReaperFixture(liveness);
        var path = fixture.Register("retained", OwnedRunRootPurpose.RunAttempt);

        var result = fixture.Reaper.Reap(0, 10);

        Assert.Equal(1, result.RetainedCount);
        Assert.Empty(fixture.FileSystem.DeletedPaths);
        var entry = fixture.Registry.ReadBatch(0, 10, Start).Single();
        Assert.Contains("recorded=", entry.LastCleanupHolder);
        Assert.Contains("observed=", entry.LastCleanupHolder);
        Assert.Equal(path, entry.CanonicalPath);
    }

    [Xunit.Fact]
    public void Failed_removal_records_holder_and_retries_only_after_backoff()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Gone);
        var path = fixture.Register("blocked", OwnedRunRootPurpose.RunAttempt);
        fixture.FileSystem.DeleteFailure = new IOException("scanner holds artifacts.dll");

        var first = fixture.Reaper.Reap(0, 10);
        var beforeBackoff = fixture.Reaper.Reap(0, 10);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = fixture.Reaper.Reap(0, 10);

        Assert.Equal(1, first.FailedCount);
        Assert.Equal(0, beforeBackoff.ProcessedCount);
        Assert.Equal(1, second.FailedCount);
        var entry = fixture.Registry.ReadBatch(0, 10, fixture.Clock.GetUtcNow().AddMinutes(5)).Single();
        Assert.Equal(2, entry.CleanupAttemptCount);
        Assert.Contains("scanner holds artifacts.dll", entry.LastCleanupHolder);
        Assert.Equal(path, entry.CanonicalPath);
    }

    [Xunit.Fact]
    public void Due_failed_removal_is_retried_when_newer_pages_remain_full()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Gone);
        var blocked = fixture.Register("blocked", OwnedRunRootPurpose.RunAttempt);
        fixture.FileSystem.DeleteFailure = new IOException("scanner holds artifacts.dll");
        var failed = fixture.Reaper.Reap(0, 1);
        fixture.FileSystem.DeleteFailure = null;
        var newer = fixture.Register("newer", OwnedRunRootPurpose.RunAttempt);
        fixture.Registry.Release(newer, OwnedRunRootReleaseOutcome.Succeeded, Start);
        var advanced = fixture.Reaper.Reap(failed.NextCursor, 1);
        var newest = fixture.Register("newest", OwnedRunRootPurpose.RunAttempt);
        fixture.Registry.Release(newest, OwnedRunRootReleaseOutcome.Succeeded, Start);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var retry = fixture.Reaper.Reap(advanced.NextCursor, 1);

        Assert.Equal(1, retry.RemovedCount);
        Assert.Contains(blocked, fixture.FileSystem.DeletedPaths);
        Assert.DoesNotContain(newest, fixture.FileSystem.DeletedPaths);
    }

    [Xunit.Fact]
    public void Reaper_is_bounded_and_resumes_from_cursor()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Live);
        for (var index = 0; index < 7; index++)
        {
            var path = fixture.Register($"released-{index}", OwnedRunRootPurpose.RunAttempt);
            fixture.Registry.Release(path, OwnedRunRootReleaseOutcome.Succeeded, Start);
        }

        var first = fixture.Reaper.Reap(0, 3);
        var second = fixture.Reaper.Reap(first.NextCursor, 3);
        var third = fixture.Reaper.Reap(second.NextCursor, 3);

        Assert.Equal((3, 3, 1), (first.ProcessedCount, second.ProcessedCount, third.ProcessedCount));
        Assert.True(first.NextCursor > 0);
        Assert.True(second.NextCursor > first.NextCursor);
        Assert.Equal(0, third.NextCursor);
        Assert.Equal(7, fixture.FileSystem.DeletedPaths.Count);
    }

    [Xunit.Fact]
    public void Unregistered_roots_are_reported_without_a_delete_capability()
    {
        using var fixture = new ReaperFixture(OwnedRunRootOwnerLiveness.Live);
        var registered = fixture.Register("registered", OwnedRunRootPurpose.RunAttempt);
        var legacy = fixture.FileSystem.Add("legacy");

        var result = fixture.Observer.ObserveUnregisteredRoots(0, 10);

        Assert.Equal(2, result.ExaminedCount);
        Assert.Single(result.UnregisteredRoots);
        Assert.Contains(legacy, result.UnregisteredRoots[0]);
        Assert.DoesNotContain(registered, result.UnregisteredRoots);
        Assert.Empty(fixture.FileSystem.DeletedPaths);
        Assert.Contains(legacy, fixture.FileSystem.Paths);
        Assert.Equal(10, fixture.FileSystem.LastEnumerationLimit);
    }

    private sealed class ReaperFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcg-owned-reaper-tests", Guid.NewGuid().ToString("N"));

        public ReaperFixture(OwnedRunRootOwnerLiveness liveness)
        {
            Directory.CreateDirectory(_directory);
            var databasePath = Path.Combine(_directory, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(databasePath);
            Registry = new OwnedRunRootRegistry(databasePath);
            StorageRoot = new DotnetBuildStorageRoot(Path.Combine(_directory, "builds"));
            Clock = new ManualTimeProvider(Start);
            FileSystem = new FakeFileSystem(StorageRoot.RootPath);
            var inspector = new FakeInspector(liveness);
            Reaper = new OwnedRunRootReaper(
                Registry,
                StorageRoot,
                Clock,
                inspector,
                FileSystem,
                TimeSpan.FromMinutes(5));
            Observer = new OwnedRunRootObserver(Registry, StorageRoot, FileSystem);
        }

        public OwnedRunRootRegistry Registry { get; }
        public DotnetBuildStorageRoot StorageRoot { get; }
        public ManualTimeProvider Clock { get; }
        public FakeFileSystem FileSystem { get; }
        public OwnedRunRootReaper Reaper { get; }
        public OwnedRunRootObserver Observer { get; }

        public string Register(string name, OwnedRunRootPurpose purpose)
        {
            var path = FileSystem.Add(name);
            Registry.Register(
                path,
                purpose,
                new SpawnProcessIdentity(12345, Start.AddHours(-1), @"C:\recorded\dotnet.exe"),
                null,
                null,
                Start);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeInspector(OwnedRunRootOwnerLiveness liveness) : IOwnedRunRootProcessInspector
    {
        public OwnedRunRootOwnerInspection Inspect(SpawnProcessIdentity recordedOwner) =>
            new(
                liveness,
                $"owner pid={recordedOwner.ProcessId} start-time mismatch recorded={recordedOwner.StartedAt:O} observed={recordedOwner.StartedAt.AddHours(1):O}");
    }

    private sealed class FakeFileSystem(string storageRoot) : IOwnedRunRootFileSystem, IOwnedRunRootDirectoryEnumerator
    {
        public HashSet<string> Paths { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public List<string> DeletedPaths { get; } = [];
        public Exception? DeleteFailure { get; set; }
        public int LastEnumerationLimit { get; private set; }

        public string Add(string name)
        {
            var path = Path.Combine(storageRoot, "runs", name);
            Paths.Add(path);
            return path;
        }

        public bool DirectoryExists(string path) => Paths.Contains(path);

        public void DeleteDirectory(string path)
        {
            if (DeleteFailure is not null)
                throw DeleteFailure;
            DeletedPaths.Add(path);
            Paths.Remove(path);
        }

        public OwnedRunRootDirectoryPage EnumerateBuildRoots(string _, int afterIndex, int maxRoots)
        {
            LastEnumerationLimit = maxRoots;
            var page = Paths.Order().Skip(afterIndex).Take(maxRoots + 1).ToArray();
            var hasMore = page.Length > maxRoots;
            var roots = hasMore ? page[..maxRoots] : page;
            return new OwnedRunRootDirectoryPage(roots, hasMore ? afterIndex + roots.Length : 0);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
