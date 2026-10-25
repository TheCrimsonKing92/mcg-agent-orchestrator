using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private payload/cache roots and AsyncLocal console capture; no compiler or global mutation.
public sealed class DotnetBaseBuildCacheEvictionProtectionTests : IDisposable
{
    private static readonly string MainSha = new('a', 40);
    private static readonly string[] Projects = ["src/A/A.csproj", "src/B/B.csproj", "src/C/C.csproj",
        "src/D/D.csproj", "src/E/E.csproj"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eviction-protection", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _entries = [];
    private string CacheRoot => Path.Combine(_root, "cache");
    private string Artifacts => Path.Combine(_root, "artifacts");

    [Fact]
    public void Publish_WrittenAndReusedEntries_SurviveUntilUnprotectedProbe()
    {
        Seed();
        var identity = Identity();
        DotnetBaseBuildCachePublishResult first = null!;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            first = Cache().Publish(MainSha, Artifacts, Projects[..3], identity));
        Assert.Equal(3, first.Projects.Count);
        Assert.All(first.Projects, receipt => Assert.Equal("published", receipt.Status));
        Assert.Empty(first.Evictions);
        AssertEntries(identity, Projects[..3]);
        var lines = StoreLines(output);
        Assert.Equal(3, lines.Length);
        foreach (var project in Projects[..3])
        {
            var line = Assert.Single(lines, value => value.Contains($" project={project} ", StringComparison.Ordinal));
            var entry = Regex.Match(line, @"\bentry=(\S+)").Groups[1].Value;
            Assert.Equal(Path.GetFileName(Entry(identity, project)), entry);
            Assert.True(Directory.Exists(Path.Combine(CacheRoot, entry)));
            Assert.Contains($" main_sha={MainSha} files=2 bytes=6", line);
        }

        // Explicit ordering avoids timestamp ties, without pacing the test on the wall clock.
        var timestamp = DateTime.UtcNow.AddHours(-1);
        SetOrder(identity, timestamp, Projects[..3]);
        var secondCache = Cache();
        var hit = secondCache.Probe(MainSha, [Projects[2]], identity);
        Assert.Equal("hit", Assert.Single(hit.Projects).Status);
        Assert.Equal(new[] { Eviction(identity, Projects[0]) }, hit.Evictions);
        var next = secondCache.Publish(MainSha, Artifacts, Projects[3..], identity, reusedProjects: [Projects[2]]);
        Assert.All(next.Projects, receipt => Assert.Equal("published", receipt.Status));
        Assert.Equal(new[] { Eviction(identity, Projects[1]) }, next.Evictions);
        AssertEntries(identity, Projects[2..]);

        SetOrder(identity, timestamp, Projects[2..]);
        var third = Cache().Probe(MainSha, Projects[3..], identity);
        Assert.True(third.AllHit);
        Assert.Equal(new[] { Eviction(identity, Projects[2]) }, third.Evictions);
        AssertEntries(identity, Projects[3..]);
    }

    [Fact]
    public void Publish_AlreadyPublishedTouch_EmitsNoStoreLine()
    {
        Seed();
        var cache = Cache();
        var identity = Identity();
        cache.Publish(MainSha, Artifacts, [Projects[0]], identity);
        DotnetBaseBuildCachePublishResult touched = null!;
        var output = AsyncLocalConsoleRouter.Capture(() =>
            touched = cache.Publish(MainSha, Artifacts, [Projects[0]], identity));
        var receipt = Assert.Single(touched.Projects);
        Assert.Equal("hit", receipt.Status);
        Assert.Equal("already-published", receipt.Reason);
        Assert.Empty(StoreLines(output));
        AssertEntries(identity, [Projects[0]]);
    }

    [Fact]
    public void Publish_LaterCallWithEmptyProtection_TrimsEarlierProtectedEntry()
    {
        Seed();
        var cache = Cache();
        var identity = Identity();
        cache.Publish(MainSha, Artifacts, Projects[..3], identity);
        AssertEntries(identity, Projects[..3]);
        SetOrder(identity, DateTime.UtcNow.AddHours(-1), Projects[..3]);
        var later = cache.Publish(MainSha, Artifacts, [], identity, reusedProjects: []);
        Assert.Empty(later.Projects);
        Assert.Equal(new[] { Eviction(identity, Projects[0]) }, later.Evictions);
        AssertEntries(identity, Projects[1..3]);
    }

    [Fact]
    public void Publish_ReusedProject_ProtectsOnlyItsCurrentIdentityEntry()
    {
        Seed();
        var cache = Cache();
        var old = Identity();
        cache.Publish(MainSha, Artifacts, Projects[..2], old);
        SetOrder(old, DateTime.UtcNow.AddHours(-1), Projects[..2]);
        var current = Identity(firstKey: new string('f', 64));
        var result = cache.Publish(MainSha, Artifacts, [Projects[2]], current, reusedProjects: [Projects[0]]);
        // The requested A key has no entry. It cannot protect the old A key.
        Assert.Equal(new[] { Eviction(old, Projects[0]) }, result.Evictions);
        Assert.False(Directory.Exists(Entry(old, Projects[0])));
        AssertEntries(current, Projects[1..3]);
    }

    private DotnetBaseBuildCache Cache() => new(CacheRoot, maxEntries: 2);

    private static DotnetBaseBuildCacheIdentity Identity(string? firstKey = null) =>
        new(new string('0', 64), ProjectKeys: string.Join(";", Projects.Select((project, index) =>
            $"{project}={(index == 0 && firstKey is not null ? firstKey : new string((char)('a' + index), 64))}")));

    private void Seed()
    {
        foreach (var project in Projects)
        foreach (var kind in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(Artifacts, kind, Path.GetFileNameWithoutExtension(project));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "payload.bin"), [1, 2, 3]);
        }
    }

    private string Entry(DotnetBaseBuildCacheIdentity identity, string project)
    {
        Assert.True(DotnetBaseBuildCacheProjectKeys.TryGet(identity.ProjectKeys, project, out var key));
        if (!_entries.TryGetValue(key, out var path))
            _entries[key] = path = Assert.Single(Directory.GetDirectories(CacheRoot, "*-" + key));
        return path;
    }

    private string Eviction(DotnetBaseBuildCacheIdentity identity, string project) =>
        $"size:{MainSha}/{Path.GetFileName(Entry(identity, project))}";

    private void SetOrder(DotnetBaseBuildCacheIdentity identity, DateTime start, string[] projects)
    {
        for (var index = 0; index < projects.Length; index++)
            Directory.SetLastWriteTimeUtc(Entry(identity, projects[index]), start.AddMinutes(index));
    }

    private void AssertEntries(DotnetBaseBuildCacheIdentity identity, string[] projects)
    {
        var expected = projects.Select(project => Entry(identity, project)).Order(StringComparer.Ordinal).ToArray();
        var actual = Directory.GetDirectories(CacheRoot).Where(path => Path.GetFileName(path) != "_staging")
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    private static string[] StoreLines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("BASE_BUILD_CACHE_STORE ", StringComparison.Ordinal)).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
