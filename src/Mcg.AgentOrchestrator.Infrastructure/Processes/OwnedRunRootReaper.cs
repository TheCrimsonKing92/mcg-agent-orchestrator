using System.ComponentModel;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum OwnedRunRootOwnerLiveness
{
    Live,
    Gone,
    Mismatched,
    Unknown
}

internal sealed record OwnedRunRootOwnerInspection(
    OwnedRunRootOwnerLiveness Liveness,
    string Evidence);

internal interface IOwnedRunRootProcessInspector
{
    OwnedRunRootOwnerInspection Inspect(SpawnProcessIdentity recordedOwner);
}

internal interface IOwnedRunRootFileSystem
{
    bool DirectoryExists(string path);
    void DeleteDirectory(string path);
}

internal interface IOwnedRunRootDirectoryEnumerator
{
    OwnedRunRootDirectoryPage EnumerateBuildRoots(string storageRoot, int afterIndex, int maxRoots);
}

internal sealed record OwnedRunRootDirectoryPage(IReadOnlyList<string> Roots, int NextCursor);

internal sealed record OwnedRunRootReapResult(
    int ProcessedCount,
    int RemovedCount,
    int RetainedCount,
    int FailedCount,
    long NextCursor);

internal sealed class OwnedRunRootReaper(
    IOwnedRunRootStore store,
    DotnetBuildStorageRoot storageRoot,
    TimeProvider timeProvider,
    IOwnedRunRootProcessInspector processInspector,
    IOwnedRunRootFileSystem fileSystem,
    TimeSpan retryBackoff)
{
    public OwnedRunRootReapResult Reap(long afterId, int maxRoots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRoots, 1);
        var now = timeProvider.GetUtcNow();
        var entries = store.ReadBatch(afterId, maxRoots, now);
        var removed = 0;
        var retained = 0;
        var failed = 0;

        foreach (var entry in entries)
        {
            if (!storageRoot.ContainsPath(entry.CanonicalPath))
            {
                RecordFailure(entry, "registered path is outside the configured build storage root", now);
                failed++;
                continue;
            }

            var releasedAttempt = entry.Purpose == OwnedRunRootPurpose.RunAttempt && entry.ReleasedAt is not null;
            if (!releasedAttempt)
            {
                var inspection = processInspector.Inspect(entry.Owner);
                if (inspection.Liveness != OwnedRunRootOwnerLiveness.Gone)
                {
                    store.RecordRetention(entry.CanonicalPath, inspection.Evidence, now);
                    retained++;
                    continue;
                }
            }

            try
            {
                if (fileSystem.DirectoryExists(entry.CanonicalPath))
                    fileSystem.DeleteDirectory(entry.CanonicalPath);
                store.MarkRemoved(entry.CanonicalPath, now);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordFailure(entry, $"{ex.GetType().Name}: {ex.Message}", now);
                failed++;
            }
        }

        var nextCursor = entries.Count < maxRoots || entries.Count == 0
            ? 0
            : Math.Max(afterId, entries.Max(entry => entry.Id));
        return new OwnedRunRootReapResult(entries.Count, removed, retained, failed, nextCursor);
    }

    private void RecordFailure(OwnedRunRootEntry entry, string evidence, DateTimeOffset now) =>
        store.MarkCleanupFailure(entry.CanonicalPath, evidence, now.Add(retryBackoff), now);
}

internal sealed record OwnedRunRootObservationResult(
    int ExaminedCount,
    IReadOnlyList<string> UnregisteredRoots,
    int NextCursor);

internal sealed class OwnedRunRootObservationLedger
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _reported =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public bool TryMarkReported(string canonicalPath) => _reported.TryAdd(canonicalPath, 0);
}

internal sealed class OwnedRunRootObserver(
    IOwnedRunRootStore store,
    DotnetBuildStorageRoot storageRoot,
    IOwnedRunRootDirectoryEnumerator directoryEnumerator,
    OwnedRunRootObservationLedger? ledger = null)
{
    public OwnedRunRootObservationResult ObserveUnregisteredRoots(int afterIndex, int maxRoots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRoots, 1);
        var page = directoryEnumerator.EnumerateBuildRoots(storageRoot.RootPath, afterIndex, maxRoots);
        var canonicalPaths = page.Roots
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToArray();
        var registered = store.ReadRegisteredPaths(canonicalPaths);
        var unregistered = canonicalPaths
            .Where(path => !registered.Contains(path) && (ledger?.TryMarkReported(path) ?? true))
            .Select(path => $"observe-only unregistered build root: {path}")
            .ToArray();
        return new OwnedRunRootObservationResult(canonicalPaths.Length, unregistered, page.NextCursor);
    }
}

internal sealed class SystemOwnedRunRootProcessInspector : IOwnedRunRootProcessInspector
{
    public OwnedRunRootOwnerInspection Inspect(SpawnProcessIdentity recordedOwner)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(recordedOwner.ProcessId);
            if (process.HasExited)
            {
                return new OwnedRunRootOwnerInspection(
                    OwnedRunRootOwnerLiveness.Gone,
                    $"owner pid={recordedOwner.ProcessId} has exited");
            }

            if (!SpawnProcessIdentityReader.TryRead(process, out var observed))
            {
                return new OwnedRunRootOwnerInspection(
                    OwnedRunRootOwnerLiveness.Unknown,
                    $"owner pid={recordedOwner.ProcessId} identity could not be read");
            }

            if (observed.StartedAt != recordedOwner.StartedAt)
            {
                return new OwnedRunRootOwnerInspection(
                    OwnedRunRootOwnerLiveness.Mismatched,
                    $"owner pid={recordedOwner.ProcessId} start-time mismatch recorded={recordedOwner.StartedAt:O} observed={observed.StartedAt:O}");
            }

            if (!PathsEqual(observed.ImagePath, recordedOwner.ImagePath))
            {
                return new OwnedRunRootOwnerInspection(
                    OwnedRunRootOwnerLiveness.Mismatched,
                    $"owner pid={recordedOwner.ProcessId} image mismatch recorded={recordedOwner.ImagePath} observed={observed.ImagePath}");
            }

            return new OwnedRunRootOwnerInspection(
                OwnedRunRootOwnerLiveness.Live,
                $"owner pid={recordedOwner.ProcessId} identity matches started_at={recordedOwner.StartedAt:O} image={observed.ImagePath}");
        }
        catch (ArgumentException)
        {
            return new OwnedRunRootOwnerInspection(
                OwnedRunRootOwnerLiveness.Gone,
                $"owner pid={recordedOwner.ProcessId} does not exist");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new OwnedRunRootOwnerInspection(
                OwnedRunRootOwnerLiveness.Unknown,
                $"owner pid={recordedOwner.ProcessId} could not be verified ({ex.GetType().Name})");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed class SystemOwnedRunRootFileSystem : IOwnedRunRootFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}

internal sealed class SystemOwnedRunRootDirectoryEnumerator : IOwnedRunRootDirectoryEnumerator
{
    public OwnedRunRootDirectoryPage EnumerateBuildRoots(string storageRoot, int afterIndex, int maxRoots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRoots, 1);
        var page = EnumerateImmediateChildren(Path.Combine(storageRoot, "runs"))
            .Concat(EnumerateImmediateChildren(Path.Combine(storageRoot, "goals")))
            .Skip(afterIndex)
            .Take(maxRoots + 1)
            .ToArray();
        var hasMore = page.Length > maxRoots;
        var roots = hasMore ? page[..maxRoots] : page;
        return new OwnedRunRootDirectoryPage(roots, hasMore ? afterIndex + roots.Length : 0);
    }

    private static IEnumerable<string> EnumerateImmediateChildren(string parent)
    {
        if (!Directory.Exists(parent))
            yield break;

        IEnumerator<string>? enumerator;
        try
        {
            enumerator = Directory.EnumerateDirectories(parent).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                        yield break;
                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return current;
            }
        }
    }
}
