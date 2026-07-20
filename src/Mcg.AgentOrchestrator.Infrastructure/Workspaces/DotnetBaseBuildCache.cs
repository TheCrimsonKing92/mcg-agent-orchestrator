using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record DotnetBaseBuildCacheProjectReceipt(
    string Project,
    string ProjectKey,
    string Status,
    string? Reason,
    string? ContentHash);

internal sealed record DotnetBaseBuildCacheRestoreResult(
    string MainSha,
    IReadOnlyList<DotnetBaseBuildCacheProjectReceipt> Projects,
    IReadOnlyList<string> Evictions)
{
    public bool AllHit => Projects.Count > 0 && Projects.All(project => project.Status.Equals("hit", StringComparison.Ordinal));
}

internal sealed record DotnetBaseBuildCachePublishResult(
    string MainSha,
    IReadOnlyList<DotnetBaseBuildCacheProjectReceipt> Projects,
    IReadOnlyList<string> Evictions);

internal sealed class DotnetBaseBuildCache
{
    private const string CacheDirectoryName = "base-build-cache";
    private const string StagingDirectoryName = "_staging";
    private const string ManifestFileName = "manifest.json";
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);
    private const int DefaultMaxEntries = 48;
    private readonly string _rootPath;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;

    public DotnetBaseBuildCache(string rootPath, int? maxEntries = null, TimeSpan? maxAge = null)
    {
        _rootPath = rootPath;
        _maxEntries = Math.Max(1, maxEntries ?? DefaultMaxEntries);
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    public static DotnetBaseBuildCache Default() =>
        new(DotnetBuildEnvironmentManager.BaseBuildCacheRoot());

    public static string DefaultRootPath(string isolatedRootBase) =>
        Path.Combine(isolatedRootBase, CacheDirectoryName);

    public DotnetBaseBuildCacheRestoreResult Probe(
        string mainSha,
        IReadOnlyList<string> projects)
    {
        Directory.CreateDirectory(_rootPath);
        var evictions = EvictExpiredAndOverflow();
        return new DotnetBaseBuildCacheRestoreResult(
            mainSha,
            ReadProjectReceipts(mainSha, artifactsPath: null, projects),
            evictions);
    }

    public DotnetBaseBuildCacheRestoreResult Restore(
        string mainSha,
        string artifactsPath,
        IReadOnlyList<string> projects)
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(artifactsPath);
        var evictions = EvictExpiredAndOverflow();
        return new DotnetBaseBuildCacheRestoreResult(
            mainSha,
            ReadProjectReceipts(mainSha, artifactsPath, projects),
            evictions);
    }

    private IReadOnlyList<DotnetBaseBuildCacheProjectReceipt> ReadProjectReceipts(
        string mainSha,
        string? artifactsPath,
        IReadOnlyList<string> projects)
    {
        var receipts = new List<DotnetBaseBuildCacheProjectReceipt>();

        foreach (var project in projects)
        {
            var projectKey = ProjectKey(project);
            var entryPath = EntryPath(mainSha, projectKey);
            var manifest = TryReadManifest(entryPath);
            if (manifest is null)
            {
                receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "miss", "not-found", null));
                continue;
            }

            var currentHash = HashDirectory(entryPath, excludeRootManifest: true);
            if (!string.Equals(currentHash, manifest.ContentHash, StringComparison.Ordinal))
            {
                receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "miss", "invalid", currentHash));
                continue;
            }

            if (artifactsPath is not null)
            {
                ClearProjectArtifactRoots(artifactsPath, project);
                CopyDirectory(entryPath, artifactsPath, skipRootManifest: true);
            }

            receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "hit", null, currentHash));
        }

        return receipts;
    }

    public DotnetBaseBuildCachePublishResult Publish(
        string mainSha,
        string artifactsPath,
        IReadOnlyList<string> projects)
    {
        Directory.CreateDirectory(_rootPath);
        var evictions = EvictExpiredAndOverflow();
        var receipts = new List<DotnetBaseBuildCacheProjectReceipt>();
        foreach (var project in projects)
        {
            var projectKey = ProjectKey(project);
            var sourceRoots = ProjectArtifactRoots(artifactsPath, project)
                .Where(Directory.Exists)
                .ToArray();
            if (sourceRoots.Length == 0)
            {
                receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "miss", "no-artifacts", null));
                continue;
            }

            var stagingPath = Path.Combine(_rootPath, StagingDirectoryName, $"{projectKey}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingPath);
            try
            {
                foreach (var sourceRoot in sourceRoots)
                {
                    var relativeRoot = Path.GetRelativePath(artifactsPath, sourceRoot);
                    CopyDirectory(sourceRoot, Path.Combine(stagingPath, relativeRoot));
                }

                var hash = HashDirectory(stagingPath, excludeRootManifest: false);
                File.WriteAllText(
                    Path.Combine(stagingPath, ManifestFileName),
                    JsonSerializer.Serialize(
                        new CacheEntryManifest(mainSha, project, projectKey, hash, DateTimeOffset.UtcNow),
                        new JsonSerializerOptions { WriteIndented = true }));

                var finalPath = EntryPath(mainSha, projectKey);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (!Directory.Exists(finalPath))
                {
                    Directory.Move(stagingPath, finalPath);
                    receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "published", null, hash));
                }
                else
                {
                    receipts.Add(new DotnetBaseBuildCacheProjectReceipt(project, projectKey, "hit", "already-published", hash));
                }
            }
            finally
            {
                TryDeleteDirectory(stagingPath);
            }
        }

        evictions.AddRange(EvictExpiredAndOverflow());
        return new DotnetBaseBuildCachePublishResult(mainSha, receipts, evictions);
    }

    public static string ProjectOutputHash(string artifactsPath, string project)
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), $"mcg-base-build-hash-{Guid.NewGuid():N}");
        try
        {
            foreach (var sourceRoot in ProjectArtifactRoots(artifactsPath, project).Where(Directory.Exists))
            {
                var relativeRoot = Path.GetRelativePath(artifactsPath, sourceRoot);
                CopyDirectory(sourceRoot, Path.Combine(stagingPath, relativeRoot));
            }

            return HashDirectory(stagingPath, excludeRootManifest: false);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private List<string> EvictExpiredAndOverflow()
    {
        var evicted = new List<string>();
        if (!Directory.Exists(_rootPath))
        {
            return evicted;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in EnumerateEntries()
            .Where(entry => now - entry.LastWriteUtc > _maxAge))
        {
            if (TryDeleteDirectory(entry.Path))
            {
                evicted.Add($"age:{entry.MainSha}/{entry.ProjectKey}");
            }
        }

        var remaining = EnumerateEntries()
            .OrderByDescending(entry => entry.LastWriteUtc)
            .ToArray();
        foreach (var entry in remaining.Skip(_maxEntries))
        {
            if (TryDeleteDirectory(entry.Path))
            {
                evicted.Add($"size:{entry.MainSha}/{entry.ProjectKey}");
            }
        }

        foreach (var eviction in evicted)
        {
            Console.WriteLine($"BASE_BUILD_CACHE_EVICT entry={Quote(eviction)}");
        }

        if (evicted.Count > 0)
        {
            Console.Out.Flush();
        }

        return evicted;
    }

    private IEnumerable<CacheEntry> EnumerateEntries()
    {
        foreach (var shaDirectory in Directory.EnumerateDirectories(_rootPath)
            .Where(path => !Path.GetFileName(path).Equals(StagingDirectoryName, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var projectDirectory in Directory.EnumerateDirectories(shaDirectory))
            {
                yield return new CacheEntry(
                    Path.GetFileName(shaDirectory),
                    Path.GetFileName(projectDirectory),
                    projectDirectory,
                    Directory.GetLastWriteTimeUtc(projectDirectory));
            }
        }
    }

    private CacheEntryManifest? TryReadManifest(string entryPath)
    {
        try
        {
            var manifestPath = Path.Combine(entryPath, ManifestFileName);
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<CacheEntryManifest>(File.ReadAllText(manifestPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string EntryPath(string mainSha, string projectKey) =>
        Path.Combine(_rootPath, SanitizeSegment(mainSha), projectKey);

    private static IEnumerable<string> ProjectArtifactRoots(string artifactsPath, string project)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        yield return Path.Combine(artifactsPath, "bin", projectName);
        yield return Path.Combine(artifactsPath, "obj", projectName);
    }

    private static void ClearProjectArtifactRoots(string artifactsPath, string project)
    {
        foreach (var root in ProjectArtifactRoots(artifactsPath, project))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string ProjectKey(string project)
    {
        var normalized = project.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(normalized);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
        return $"{SanitizeSegment(name)}-{hash}";
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "entry" : sanitized;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath, bool skipRootManifest = false)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (skipRootManifest && IsRootManifest(sourcePath, file))
            {
                continue;
            }

            var destination = Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string HashDirectory(string path, bool excludeRootManifest)
    {
        using var sha = SHA256.Create();
        if (!Directory.Exists(path))
        {
            return Convert.ToHexString(sha.ComputeHash([])).ToLowerInvariant();
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(file => !excludeRootManifest || !IsRootManifest(path, file))
            .OrderBy(file => Path.GetRelativePath(path, file), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var content = File.ReadAllBytes(file);
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static bool IsRootManifest(string rootPath, string filePath) =>
        Path.GetRelativePath(rootPath, filePath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase);

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Quote(string value) =>
        value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
            ? value
            : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record CacheEntry(string MainSha, string ProjectKey, string Path, DateTimeOffset LastWriteUtc);

    private sealed record CacheEntryManifest(
        string MainSha,
        string Project,
        string ProjectKey,
        string ContentHash,
        DateTimeOffset CreatedAt);
}
