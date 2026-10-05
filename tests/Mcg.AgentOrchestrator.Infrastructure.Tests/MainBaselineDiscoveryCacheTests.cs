using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fact owns its cache directory and uses a fixed clock.
public sealed class MainBaselineDiscoveryCacheTests
{
    [Xunit.Fact]
    public void RoundTripPreservesParserInputsAndEntriesAreDisposable()
    {
        using var fixture = new Fixture();
        var result = new GoalAcceptanceVerifier.CommandResult(0, "Sample.Tests.Passes", Stderr: "diagnostic");
        fixture.Cache.TryWrite(fixture.Key, result);
        Assert.Equal(result, fixture.Cache.TryRead(fixture.Key));
        Assert.Equal(fixture.Key.EntryName + ".json", Path.GetFileName(fixture.Path));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        File.Delete(fixture.Path);
        Assert.Null(fixture.Cache.TryRead(fixture.Key));
    }

    [Xunit.Fact]
    public void KeyStripsBothSwitchAndEmbeddedAssemblyArtifactsPaths()
    {
        using var fixture = new Fixture();
        var firstArtifacts = Path.Combine(fixture.Root, "gate-one", "main-coverage-baseline");
        var secondArtifacts = Path.Combine(fixture.Root, "gate-two", "main-coverage-baseline");
        string[] Arguments(string artifacts) =>
            ["dotnet", Path.Combine(artifacts, "bin", "Sample.Tests", "debug", "Sample.Tests.dll"),
                "--artifacts-path", artifacts, "--list-tests", "json"];
        var first = MainBaselineDiscoveryCache.CreateKey("tree", fixture.Root, "tests/Sample.Tests.csproj", "Debug",
            Arguments(firstArtifacts), firstArtifacts);
        var second = MainBaselineDiscoveryCache.CreateKey("tree", fixture.Root, "tests/Sample.Tests.csproj", "Debug",
            Arguments(secondArtifacts), secondArtifacts);
        Assert.Equal(first, second);
        Assert.Equal(first.EntryName, second.EntryName);
        Assert.NotEqual(first, first with { TreeDigest = "other-tree" });
        Assert.NotEqual(first.EntryName, (first with { TreeDigest = "other-tree" }).EntryName);
        Assert.NotEqual(first.EntryName, (first with { MainWorktreePath = "other-main" }).EntryName);
        Assert.NotEqual(first.EntryName, (first with { Project = "other-project" }).EntryName);
        Assert.NotEqual(first.EntryName, (first with { Configuration = "Release" }).EntryName);
        Assert.NotEqual(first.EntryName, (first with { ArgumentsFingerprint = "other-arguments" }).EntryName);
        Assert.NotEqual(first.ArgumentsFingerprint, MainBaselineDiscoveryCache.CreateKey("tree", fixture.Root,
            "tests/Sample.Tests.csproj", "Debug", [.. Arguments(firstArtifacts), "--filter", "changed"], firstArtifacts).ArgumentsFingerprint);
    }

    [Xunit.Theory]
    [Xunit.InlineData("TreeDigest")]
    [Xunit.InlineData("MainWorktreePath")]
    [Xunit.InlineData("Project")]
    [Xunit.InlineData("Configuration")]
    [Xunit.InlineData("ArgumentsFingerprint")]
    public void MismatchedKeyIsAMissEvenWithValidPayloadHash(string field)
    {
        using var fixture = new Fixture();
        fixture.Write();
        fixture.Rewrite(_ => { }, recomputeHash: true);
        Assert.NotNull(fixture.Cache.TryRead(fixture.Key));
        fixture.Rewrite(entry => entry["Payload"]!["Key"]![field] = "mismatched", recomputeHash: true);
        Assert.Null(fixture.Cache.TryRead(fixture.Key));
        fixture.Write();
        Assert.NotNull(fixture.Cache.TryRead(fixture.Key));
    }

    [Xunit.Theory]
    [Xunit.InlineData("hash")]
    [Xunit.InlineData("schema")]
    [Xunit.InlineData("truncated")]
    [Xunit.InlineData("missing-payload")]
    public void MalformedEntriesAreMissesAndCanBeReplaced(string corruption)
    {
        using var fixture = new Fixture();
        fixture.Write();
        switch (corruption)
        {
            case "hash": fixture.Rewrite(entry => entry["Payload"]!["Output"] = "changed", false); break;
            case "schema": fixture.Rewrite(entry => entry["Payload"]!["Schema"] = -1, true); break;
            case "truncated": File.WriteAllText(fixture.Path, "{"); break;
            case "missing-payload": File.WriteAllText(fixture.Path, "{}"); break;
        }
        Assert.Null(fixture.Cache.TryRead(fixture.Key));
        fixture.Write();
        Assert.NotNull(fixture.Cache.TryRead(fixture.Key));
    }

    [Xunit.Fact]
    public void UnreadableEntryAndUnwritableRootDoNotAffectCaller()
    {
        using var fixture = new Fixture();
        fixture.Write();
        using (var held = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(fixture.Cache.TryRead(fixture.Key));
        var blockedRoot = Path.Combine(fixture.Root, "file-as-directory");
        File.WriteAllText(blockedRoot, "blocked");
        new MainBaselineDiscoveryCache(blockedRoot).TryWrite(fixture.Key, new(0, "Sample.Tests.Passes"));
        Assert.Equal("blocked", File.ReadAllText(blockedRoot));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, false)]
    [Xunit.InlineData(0, true)]
    public void FailedOrTimedOutResultsWriteNothing(int exitCode, bool timedOut)
    {
        using var fixture = new Fixture();
        fixture.Cache.TryWrite(fixture.Key, new(exitCode, "failed", TimedOut: timedOut));
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Xunit.Fact]
    public void CountEvictionAndAgeExpiryUseFilesAndControlledClock()
    {
        using var fixture = new Fixture(maxEntries: 1);
        fixture.Write();
        File.SetLastWriteTimeUtc(fixture.Path, fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        var next = fixture.Key with { TreeDigest = "second-tree" };
        fixture.Cache.TryWrite(next, new(0, "Sample.Tests.Passes"));
        Assert.False(File.Exists(fixture.Path));
        Assert.NotNull(fixture.Cache.TryRead(next));
        fixture.Clock.Now += TimeSpan.FromDays(8);
        Assert.Null(fixture.Cache.TryRead(next));
        var nextPath = Path.Combine(fixture.Root, next.EntryName + ".json");
        File.SetLastWriteTimeUtc(nextPath, fixture.Clock.Now.UtcDateTime.AddDays(-8));
        var latest = next with { TreeDigest = "third-tree" };
        fixture.Cache.TryWrite(latest, new(0, "Sample.Tests.Passes"));
        Assert.False(File.Exists(nextPath));
        Assert.NotNull(fixture.Cache.TryRead(latest));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.json"));
    }

    private sealed class FixedClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcg-discovery-cache-unit", Guid.NewGuid().ToString("N"));
        internal FixedClock Clock { get; } = new();
        internal MainBaselineDiscoveryCache Cache { get; }
        internal MainBaselineDiscoveryCacheKey Key { get; }
        internal string Path => System.IO.Path.Combine(Root, Key.EntryName + ".json");

        internal Fixture(int maxEntries = 96)
        {
            Cache = new(Root, maxEntries: maxEntries, clock: Clock);
            Key = MainBaselineDiscoveryCache.CreateKey("tree", Root, "tests/Sample.Tests.csproj", "Debug", ["--list-tests"], Root);
        }

        internal void Write()
        {
            Cache.TryWrite(Key, new(0, "Sample.Tests.Passes"));
            // Pin file metadata too: eviction must not depend on the machine clock.
            File.SetLastWriteTimeUtc(Path, Clock.GetUtcNow().UtcDateTime);
        }

        internal void Rewrite(Action<JsonNode> mutate, bool recomputeHash)
        {
            var entry = JsonNode.Parse(File.ReadAllText(Path))!;
            mutate(entry);
            if (recomputeHash) entry["ContentHash"] = MainBaselineDiscoveryCache.Hash(entry["Payload"]!.ToJsonString(
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.WriteAllText(Path, entry.ToJsonString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
