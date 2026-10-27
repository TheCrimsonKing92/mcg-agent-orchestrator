using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A stamp proves only that previously settled metadata has not changed. Holder
// liveness is never cached for an attempt that still reserves capacity.
internal sealed class SettledAttemptCensusMemo
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    // Partition by directory so pruning each goal never scans every other goal's entries.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTime LastWriteUtc, long Length)>> _directories = new(PathComparer);

    internal bool IsSettled(string path, DateTime lastWriteUtc, long length)
    {
        var canonicalPath = Path.GetFullPath(path);
        return _directories.TryGetValue(Path.GetDirectoryName(canonicalPath)!, out var entries) &&
            entries.TryGetValue(canonicalPath, out var stamp) && stamp == (lastWriteUtc, length);
    }

    internal void Record(string path, DateTime lastWriteUtc, long length)
    {
        var canonicalPath = Path.GetFullPath(path);
        _directories.GetOrAdd(Path.GetDirectoryName(canonicalPath)!, _ => new(PathComparer))[canonicalPath] = (lastWriteUtc, length);
    }

    internal void DropMissing(string directory, IEnumerable<string> presentPaths)
    {
        var canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!_directories.TryGetValue(canonicalDirectory, out var entries)) return;
        var present = presentPaths.Select(Path.GetFullPath).ToHashSet(PathComparer);
        foreach (var entry in entries)
            if (!present.Contains(entry.Key))
                ((ICollection<KeyValuePair<string, (DateTime LastWriteUtc, long Length)>>)entries).Remove(entry);
    }
}
