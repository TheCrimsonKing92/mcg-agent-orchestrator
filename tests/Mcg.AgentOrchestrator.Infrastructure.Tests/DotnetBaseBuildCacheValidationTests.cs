using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: each case owns its cache and copied output directories.
public sealed class DotnetBaseBuildCacheValidationTests : IDisposable
{
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cache-validation", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("same-stat")]
    [InlineData("project")]
    [InlineData("main")]
    [InlineData("key")]
    [InlineData("schema")]
    [InlineData("identity")]
    [InlineData("legacy")]
    [InlineData("partial")]
    public void InvalidEntry_IsAMissWithoutRestoringOutputs(string damage)
    {
        var artifacts = Path.Combine(_root, "source");
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        WriteOutputs(artifacts);
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, artifacts, [ExecutionProject], new("fixture-v1")).Projects).Status);
        var manifestPath = Directory.EnumerateFiles(Path.Combine(_root, "cache"), "manifest.json", SearchOption.AllDirectories).Single();
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        switch (damage)
        {
            case "missing": File.Delete(manifestPath); break;
            case "truncated": File.WriteAllText(manifestPath, "{"); break;
            case "hash": manifest["ContentHash"] = new string('0', 64); break;
            case "project": manifest["Project"] = CoreProject; break;
            case "main": manifest["MainSha"] = new string('b', 40); break;
            case "key": manifest["ProjectKey"] = "wrong"; break;
            case "schema": manifest["SchemaVersion"] = 999; break;
            case "identity": manifest["CompatibilityFingerprint"] = "different-inputs"; break;
            case "legacy": manifest.Remove("CompatibilityFingerprint"); break;
            case "partial":
                Directory.Delete(Path.Combine(Path.GetDirectoryName(manifestPath)!, "obj"), recursive: true);
                break;
            case "same-stat":
                var file = Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(manifestPath)!, "bin"), "*", SearchOption.AllDirectories).Single();
                var time = File.GetLastWriteTimeUtc(file);
                File.WriteAllText(file, "other");
                File.SetLastWriteTimeUtc(file, time);
                break;
        }
        if (damage is not "missing" and not "truncated") File.WriteAllText(manifestPath, manifest.ToJsonString());

        var destination = Path.Combine(_root, "destination");
        var restore = cache.Restore(MainSha, destination, [ExecutionProject], new("fixture-v1"));

        Assert.False(restore.AllHit);
        Assert.Equal("miss", Assert.Single(restore.Projects).Status);
        Assert.Empty(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CompleteExecutionEntry_RestoresIndependentCopies()
    {
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var source = Path.Combine(_root, "source");
        WriteOutputs(source);
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, source, [ExecutionProject], new("fixture-v1")).Projects).Status);
        Assert.True(cache.Probe(MainSha, [ExecutionProject], new("fixture-v1")).AllHit);
        var destination = Path.Combine(_root, "destination");
        Assert.True(cache.Restore(MainSha, destination, [ExecutionProject], new("fixture-v1")).AllHit);
        Assert.Equal(DotnetBaseBuildCache.ProjectOutputHash(source, ExecutionProject), DotnetBaseBuildCache.ProjectOutputHash(destination, ExecutionProject));
        var copied = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).First();
        File.WriteAllText(copied, "mutable");
        var second = Path.Combine(_root, "second");
        Assert.True(cache.Restore(MainSha, second, [ExecutionProject], new("fixture-v1")).AllHit);
        Assert.Equal(DotnetBaseBuildCache.ProjectOutputHash(source, ExecutionProject), DotnetBaseBuildCache.ProjectOutputHash(second, ExecutionProject));
    }

    [Fact]
    public void IncompleteOutputs_AreNotPublished()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin", Path.GetFileNameWithoutExtension(ExecutionProject)));
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        Assert.Equal("no-artifacts", Assert.Single(cache.Publish(MainSha, source, [ExecutionProject], new("fixture-v1")).Projects).Reason);
        Assert.False(cache.Probe(MainSha, [ExecutionProject], new("fixture-v1")).AllHit);
    }

    [Fact]
    public void ColdBuild_ReplacesInvalidEntryForTheNextWarmRestore()
    {
        var source = Path.Combine(_root, "source");
        WriteOutputs(source);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, source, [ExecutionProject], new("fixture-v1")).Projects).Status);
        var manifestPath = Directory.EnumerateFiles(Path.Combine(_root, "cache"), "manifest.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(manifestPath, "{");
        Assert.False(cache.Probe(MainSha, [ExecutionProject], new("fixture-v1")).AllHit);
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, source, [ExecutionProject], new("fixture-v1")).Projects).Status);
        Assert.True(cache.Restore(MainSha, Path.Combine(_root, "destination"), [ExecutionProject], new("fixture-v1")).AllHit);
    }

    [Fact]
    public void CompatibilityChange_MissesWithoutOverwritingTheCompatibleEntry()
    {
        var source = Path.Combine(_root, "source");
        WriteOutputs(source);
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        Assert.Equal("published", Assert.Single(cache.Publish(MainSha, source, [ExecutionProject], new("debug-inputs")).Projects).Status);
        Assert.False(cache.Probe(MainSha, [ExecutionProject], new("release-inputs")).AllHit);
        Assert.True(cache.Restore(MainSha, Path.Combine(_root, "destination"), [ExecutionProject], new("debug-inputs")).AllHit);
    }

    private static void WriteOutputs(string root)
    {
        foreach (var kind in new[] { "bin", "obj" })
        {
            var path = Path.Combine(root, kind, Path.GetFileNameWithoutExtension(ExecutionProject), "debug_net10.0");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "output.dll"), "bytes");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
