using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Disposable discovery inputs, never build artifacts or coverage verdicts.
internal sealed class MainBaselineDiscoveryCache
{
    internal const int SchemaVersion = 1;
    private readonly string _rootPath;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _clock;

    internal MainBaselineDiscoveryCache(string rootPath, int maxEntries = 96,
        TimeSpan? maxAge = null, TimeProvider? clock = null)
    {
        _rootPath = rootPath;
        _maxEntries = Math.Max(1, maxEntries);
        _maxAge = maxAge ?? TimeSpan.FromDays(7);
        _clock = clock ?? TimeProvider.System;
    }

    internal static string DefaultRootPath(string isolatedRootBase) =>
        Path.Combine(isolatedRootBase, "main-baseline-discovery");

    internal static MainBaselineDiscoveryCacheKey? TryCreateKey(string mainWorktreePath,
        string project, string configuration, string[] discoveryArguments, string artifactsPath)
    {
        try
        {
            return WorktreeTreeDigest.TryCompute(mainWorktreePath, out var digest, out _)
                ? CreateKey(digest, mainWorktreePath, project, configuration, discoveryArguments, artifactsPath)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static MainBaselineDiscoveryCacheKey CreateKey(string digest, string mainWorktreePath,
        string project, string configuration, string[] discoveryArguments, string artifactsPath)
    {
        var normalizedArtifacts = artifactsPath.Replace('\\', '/').TrimEnd('/');
        var arguments = discoveryArguments.Select(argument => argument.Replace('\\', '/')
            .Replace(normalizedArtifacts, "<artifacts>", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(digest, Path.GetFullPath(mainWorktreePath).Replace('\\', '/'),
            project.Replace('\\', '/'), configuration, Hash(JsonSerializer.Serialize(arguments)));
    }

    internal GoalAcceptanceVerifier.CommandResult? TryRead(MainBaselineDiscoveryCacheKey key)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(EntryPath(key)));
            if (entry?.Payload is not { } payload || payload.Schema != SchemaVersion ||
                payload.Key != key || payload.ExitCode != 0 || payload.TimedOut || payload.Output is null ||
                payload.CreatedAt < _clock.GetUtcNow() - _maxAge ||
                !string.Equals(entry.ContentHash, Hash(JsonSerializer.Serialize(payload)), StringComparison.Ordinal))
                return null;
            return new(payload.ExitCode, payload.Output, TimedOut: false, Stderr: payload.Stderr);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void TryWrite(MainBaselineDiscoveryCacheKey key, GoalAcceptanceVerifier.CommandResult result)
    {
        if (result.ExitCode != 0 || result.TimedOut) return;
        string? temporaryPath = null;
        try
        {
            if (TryRead(key) is not null) return;
            Directory.CreateDirectory(_rootPath);
            temporaryPath = Path.Combine(_rootPath, $".{Guid.NewGuid():N}.tmp");
            var payload = new Payload(SchemaVersion, key, result.ExitCode, result.Output,
                result.Stderr, false, _clock.GetUtcNow());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new Entry(payload, Hash(JsonSerializer.Serialize(payload)))));
            File.SetLastWriteTimeUtc(temporaryPath, payload.CreatedAt.UtcDateTime);
            File.Move(temporaryPath, EntryPath(key), overwrite: true);
            Evict();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cache failure cannot change the gate's result.
        }
        finally
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
        }
    }

    private string EntryPath(MainBaselineDiscoveryCacheKey key) => Path.Combine(_rootPath, key.EntryName + ".json");

    private void Evict()
    {
        var entries = new DirectoryInfo(_rootPath).GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
        var retained = 0;
        foreach (var entry in entries)
        {
            if (entry.LastWriteTimeUtc < (_clock.GetUtcNow() - _maxAge).UtcDateTime || ++retained > _maxEntries)
                TryDelete(entry.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Payload(int Schema, MainBaselineDiscoveryCacheKey Key, int ExitCode,
        string Output, string? Stderr, bool TimedOut, DateTimeOffset CreatedAt);
    private sealed record Entry(Payload Payload, string ContentHash);
}

internal sealed record MainBaselineDiscoveryCacheKey(string TreeDigest, string MainWorktreePath,
    string Project, string Configuration, string ArgumentsFingerprint)
{
    internal string EntryName => MainBaselineDiscoveryCache.Hash(JsonSerializer.Serialize(
        new { Schema = MainBaselineDiscoveryCache.SchemaVersion, Key = this }));
}

internal sealed record MainBaselineDiscoveryCacheWrite(MainBaselineDiscoveryCache Cache, MainBaselineDiscoveryCacheKey Key)
{
    internal void Publish(GoalAcceptanceVerifier.CommandResult result)
    {
        // Do not publish under the old identity if main changed during build/discovery.
        if (WorktreeTreeDigest.TryCompute(Key.MainWorktreePath, out var digest, out _) && digest == Key.TreeDigest)
            Cache.TryWrite(Key, result);
    }
}
