using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DotnetBaseBuildCacheIoTests
{
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Project = "src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj";

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_unchanged_tree_probe_performs_no_content_reads")]
    public void DotnetBaseBuildCacheUnchangedTreeProbePerformsNoContentReads()
    {
        using var fixture = new CacheFixture();
        fixture.Publish();
        fixture.ResetContentReads();

        var probe = fixture.Cache.Probe(MainSha, [Project]);

        Assert.True(probe.AllHit);
        Assert.Equal(0, fixture.ContentReads);
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_same_stat_content_tamper_is_detected_by_content_hash_fallback")]
    public void DotnetBaseBuildCacheSameStatContentTamperIsDetectedByContentHashFallback()
    {
        using var fixture = new CacheFixture();
        fixture.Publish();
        fixture.RemoveFileStatsFromManifest();
        var cachedFile = fixture.CachedFiles().First();
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(cachedFile);
        var original = File.ReadAllText(cachedFile);
        var replacement = new string(original.Reverse().ToArray());
        Assert.Equal(original.Length, replacement.Length);
        Assert.NotEqual(original, replacement);
        File.WriteAllText(cachedFile, replacement);
        File.SetLastWriteTimeUtc(cachedFile, lastWriteTimeUtc);
        Assert.Equal(lastWriteTimeUtc, File.GetLastWriteTimeUtc(cachedFile));
        fixture.ResetContentReads();

        var restorePath = Path.Combine(fixture.RootPath, "restore");
        var restore = fixture.Cache.Restore(MainSha, restorePath, [Project]);

        var receipt = Assert.Single(restore.Projects);
        Assert.Equal("miss", receipt.Status);
        Assert.Equal("invalid", receipt.Reason);
        Assert.True(fixture.ContentReads > 0);
        Assert.Empty(Directory.Exists(restorePath)
            ? Directory.EnumerateFiles(restorePath, "*", SearchOption.AllDirectories)
            : []);
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_added_file_forces_content_hash")]
    public void DotnetBaseBuildCacheAddedFileForcesContentHash()
    {
        using var fixture = new CacheFixture();
        fixture.Publish();
        File.WriteAllText(Path.Combine(fixture.EntryPath, "added.txt"), "added");
        fixture.ResetContentReads();

        var probe = fixture.Cache.Probe(MainSha, [Project]);

        Assert.Equal("invalid", Assert.Single(probe.Projects).Reason);
        Assert.True(fixture.ContentReads > 0);
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_removed_file_forces_content_hash")]
    public void DotnetBaseBuildCacheRemovedFileForcesContentHash()
    {
        using var fixture = new CacheFixture();
        fixture.Publish();
        File.Delete(fixture.CachedFiles().First());
        fixture.ResetContentReads();

        var probe = fixture.Cache.Probe(MainSha, [Project]);

        Assert.Equal("invalid", Assert.Single(probe.Projects).Reason);
        Assert.True(fixture.ContentReads > 0);
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_legacy_manifest_without_file_stats_hits_with_same_hash")]
    public void DotnetBaseBuildCacheLegacyManifestWithoutFileStatsHitsWithSameHash()
    {
        using var fixture = new CacheFixture();
        var legacyHash = LegacyHashDirectory(fixture.ArtifactsPath);
        fixture.Publish();
        var expectedHash = fixture.RemoveFileStatsFromManifest();
        fixture.ResetContentReads();

        var probe = fixture.Cache.Probe(MainSha, [Project]);

        var receipt = Assert.Single(probe.Projects);
        Assert.True(probe.AllHit);
        Assert.Equal(legacyHash, expectedHash);
        Assert.Equal(expectedHash, receipt.ContentHash);
        Assert.True(fixture.ContentReads > 0);
    }

    [Xunit.Fact(DisplayName = "DotnetBaseBuildCache_restore_copy_produces_byte_identical_destination_tree")]
    public void DotnetBaseBuildCacheRestoreCopyProducesByteIdenticalDestinationTree()
    {
        using var fixture = new CacheFixture();
        fixture.Publish();
        var restorePath = Path.Combine(fixture.RootPath, "restore");

        var restore = fixture.Cache.Restore(MainSha, restorePath, [Project]);

        Assert.True(restore.AllHit);
        Assert.Equal(
            ReadTree(fixture.ArtifactsPath),
            ReadTree(restorePath));
    }

    private static IReadOnlyList<(string Path, string Content)> ReadTree(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.Ordinal)
            .Select(file => (
                Path.GetRelativePath(path, file).Replace('\\', '/'),
                Convert.ToHexString(File.ReadAllBytes(file))))
            .ToArray();

    private static string LegacyHashDirectory(string path)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var nameBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private sealed class CacheFixture : IDisposable
    {
        private int _contentReads;

        public CacheFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "mcg-base-build-cache-io", Guid.NewGuid().ToString("N"));
            CacheRoot = Path.Combine(RootPath, "cache");
            ArtifactsPath = Path.Combine(RootPath, "artifacts");
            Cache = new DotnetBaseBuildCache(CacheRoot, contentReader: ReadContent);
            WriteArtifacts();
        }

        public string RootPath { get; }

        public string CacheRoot { get; }

        public string ArtifactsPath { get; }

        public DotnetBaseBuildCache Cache { get; }

        public int ContentReads => _contentReads;

        public string EntryPath => Path.GetDirectoryName(ManifestPath)!;

        private string ManifestPath => Directory.EnumerateFiles(CacheRoot, "manifest.json", SearchOption.AllDirectories).Single();

        public void Publish()
        {
            var receipt = Assert.Single(Cache.Publish(MainSha, ArtifactsPath, [Project]).Projects);
            Assert.Equal("published", receipt.Status);
        }

        public void ResetContentReads() => _contentReads = 0;

        public IReadOnlyList<string> CachedFiles() =>
            Directory.EnumerateFiles(EntryPath, "*", SearchOption.AllDirectories)
                .Where(file => !file.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();

        public string RemoveFileStatsFromManifest()
        {
            var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
            var contentHash = manifest["ContentHash"]!.GetValue<string>();
            Assert.True(manifest.Remove("Files"));
            File.WriteAllText(ManifestPath, manifest.ToJsonString());
            return contentHash;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private byte[] ReadContent(string path)
        {
            _contentReads++;
            return File.ReadAllBytes(path);
        }

        private void WriteArtifacts()
        {
            var projectName = Path.GetFileNameWithoutExtension(Project);
            var binPath = Path.Combine(ArtifactsPath, "bin", projectName, "debug_net10.0");
            var objPath = Path.Combine(ArtifactsPath, "obj", projectName, "debug_net10.0");
            Directory.CreateDirectory(binPath);
            Directory.CreateDirectory(objPath);
            File.WriteAllText(Path.Combine(binPath, "output.dll"), "alpha");
            File.WriteAllText(Path.Combine(objPath, "output.cache"), "beta!");
        }
    }
}
