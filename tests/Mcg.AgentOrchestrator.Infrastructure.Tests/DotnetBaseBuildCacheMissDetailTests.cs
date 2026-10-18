using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: real cache and artifacts under an independently owned temp root; injected build hooks.
public sealed class DotnetBaseBuildCacheMissDetailTests : IDisposable
{
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cache-miss-detail", Guid.NewGuid().ToString("N"));
    private string CacheRoot => Path.Combine(_root, "cache");

    [Fact]
    public void RecordedSibling_ReportsDifferingComponentWithoutChangingReason()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("recorded", "A:evaluation=1;A:imports=2"));

        var receipt = Assert.Single(cache.Probe(MainSha, [ExecutionProject], new("current", "A:evaluation=1;A:imports=3")).Projects);

        Assert.Equal("miss", receipt.Status);
        Assert.Equal("not-found", receipt.Reason);
        Assert.Equal("A:imports", receipt.MismatchedComponents);
        var restored = Assert.Single(cache.Restore(MainSha, Path.Combine(_root, "restore"), [ExecutionProject],
            new("current", "A:evaluation=1;A:imports=3")).Projects);
        Assert.Equal(receipt, restored);
    }

    [Fact]
    public void NoSibling_LeavesMismatchedComponentsNull()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        var receipt = Assert.Single(cache.Probe(MainSha, [ExecutionProject], new("current", "A:imports=3")).Projects);
        Assert.Equal("not-found", receipt.Reason);
        Assert.Null(receipt.MismatchedComponents);
    }

    [Fact]
    public void OldManifestWithoutComponents_RemainsReadableAndReportsUnrecorded()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("fixture-v1"));
        var path = Assert.Single(Directory.EnumerateFiles(CacheRoot, "manifest.json", SearchOption.AllDirectories));
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(2, manifest["SchemaVersion"]!.GetValue<int>());
        Assert.Null(manifest["CompatibilityComponents"]);
        Assert.True(manifest.Remove("CompatibilityComponents"));
        File.WriteAllText(path, manifest.ToJsonString());

        Assert.True(cache.Probe(MainSha, [ExecutionProject], new("fixture-v1")).AllHit);
        Assert.Equal("unrecorded", Assert.Single(cache.Probe(MainSha, [ExecutionProject], new("current", "A:imports=3")).Projects).MismatchedComponents);
    }

    [Fact]
    public void MultipleSiblings_SelectsFewestDifferencesBeforeOrdinalEntryName()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("a-far", "A:evaluation=2;A:imports=2"));
        Publish(cache, new("z-close", "A:evaluation=1;A:imports=2"));
        Assert.Equal("A:imports", Assert.Single(cache.Probe(MainSha, [ExecutionProject],
            new("current", "A:evaluation=1;A:imports=3")).Projects).MismatchedComponents);
    }

    [Fact]
    public void EqualDifferenceCounts_SelectsOrdinalEntryNameRegardlessOfEnumeration()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("z-last", "A:evaluation=2;A:imports=3"));
        Publish(cache, new("a-first", "A:evaluation=1;A:imports=2"));
        Assert.Equal("A:imports", Assert.Single(cache.Probe(MainSha, [ExecutionProject],
            new("current", "A:evaluation=1;A:imports=3")).Projects).MismatchedComponents);
    }

    [Fact]
    public void UnrelatedProjectAndUnreadableManifest_AreIgnored()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("another-project", "A:imports=3"), CoreProject);
        Publish(cache, new("malformed", "A:imports=3"));
        var broken = Directory.EnumerateFiles(CacheRoot, "manifest.json", SearchOption.AllDirectories)
            .Single(path => JsonNode.Parse(File.ReadAllText(path))!["Project"]!.GetValue<string>() == ExecutionProject);
        File.WriteAllText(broken, "{");
        Assert.Null(Assert.Single(cache.Probe(MainSha, [ExecutionProject], new("current", "A:imports=3")).Projects).MismatchedComponents);

        Publish(cache, new("recorded", "A:imports=2"));
        Assert.Equal("A:imports", Assert.Single(cache.Probe(MainSha, [ExecutionProject],
            new("current", "A:imports=3")).Projects).MismatchedComponents);
    }

    [Theory]
    [InlineData("Z=1;A=2", "Z=3;B=2", "A+B+Z")]
    [InlineData(";A;;", "A=", "")]
    [InlineData("A", "", "A")]
    [InlineData("A=1", "A=1", "")]
    public void DifferingComponents_IncludesMissingNamesAndSortsOrdinally(string recorded, string current, string expected) =>
        Assert.Equal(expected, DotnetBaseBuildCacheMissDetail.DifferingComponents(recorded, current));

    [Fact]
    public void RecordedMatchingComponents_ReturnEmptyRatherThanUnrecorded()
    {
        Assert.Equal("", DotnetBaseBuildCacheMissDetail.Describe([("entry", "A=1"), ("legacy", null)], "A=1"));
    }

    [Fact]
    public async Task BuildPhase_MissReasonIsOneJournalCompatibleField()
    {
        var cache = new DotnetBaseBuildCache(CacheRoot);
        Publish(cache, new("recorded", "A:evaluation=1;A:imports=2"));
        var plan = new DotnetBaseBuildCachePlan(MainSha, [ExecutionProject], [InfrastructureTestsProject], [ExecutionProject]);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", InfrastructureTestsProject], plan);
        var owned = Path.Combine(_root, "owned");
        var environment = new DotnetBuildEnvironment("owned", owned, owned, Path.Combine(owned, "lock"), [], "fixture");
        var builds = 0;
        var resolutions = 0;
        var hooks = new AcceptanceDotnetBuildPhaseHooks((arguments, _, prepare, supplied) =>
        {
            Assert.Null(prepare);
            Assert.Same(environment, supplied);
            Assert.Equal(phase.BuildArguments, arguments);
            builds++;
            WriteOutputs(environment.ArtifactsPath, ExecutionProject);
            return Task.FromResult((new AcceptanceCheckResult("build", true, 0, null, ResultSummary: "fixture-complete"), false));
        }, _ => environment, label => label, () => cache, (_, _) =>
        {
            resolutions++;
            return Task.FromResult<DotnetBaseBuildCacheIdentity?>(new("current", "A:evaluation=1;A:imports=3"));
        });

        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);

        Assert.True(result.Run.Result.Passed);
        Assert.Equal(1, builds);
        Assert.Equal(2, resolutions);
        var summary = Assert.IsType<string>(result.Run.Result.ResultSummary);
        Assert.Contains("Execution=not-found[A:imports]", summary);
        var receipt = summary.Split(';')[0];
        Assert.StartsWith("base-build-cache ", receipt);
        Assert.Equal("miss_reasons=Execution=not-found[A:imports]",
            Assert.Single(summary.Split(' ').Where(field => field.StartsWith("miss_reasons=", StringComparison.Ordinal))));
        Assert.DoesNotContain(";", receipt);
        Assert.True(receipt.IndexOf("built_projects=", StringComparison.Ordinal) < receipt.IndexOf("miss_reasons=", StringComparison.Ordinal));
        Assert.True(receipt.IndexOf("miss_reasons=", StringComparison.Ordinal) < receipt.IndexOf("evictions=", StringComparison.Ordinal));
        Assert.Contains("evictions=none; fixture-complete", summary);
    }

    private void Publish(DotnetBaseBuildCache cache, DotnetBaseBuildCacheIdentity identity, string project = ExecutionProject)
    {
        var seed = Path.Combine(_root, "seed");
        WriteOutputs(seed, project);
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, seed, [project], identity).Projects).Status);
    }

    private static void WriteOutputs(string root, string project)
    {
        foreach (var kind in new[] { "bin", "obj" })
        {
            var directory = Path.Combine(root, kind, Path.GetFileNameWithoutExtension(project), "debug_net10.0");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "cache.txt"), "fixture-output");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
