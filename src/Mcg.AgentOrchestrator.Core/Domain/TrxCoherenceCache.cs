using System.Collections.Concurrent;
using System.Xml;

namespace Mcg.AgentOrchestrator.Core;

// Owns only the per-file coherence verdict; receipt content hashes remain uncached.
internal static class TrxCoherenceCache
{
    private const int MaxEntries = 2048;
    private static readonly ConcurrentDictionary<FileStamp, bool> Entries = new();
    private static readonly object InsertionLock = new();
    private static long loadCount;

    internal static long LoadCount => Interlocked.Read(ref loadCount);

    internal static void Reset()
    {
        lock (InsertionLock)
        {
            Entries.Clear();
            Interlocked.Exchange(ref loadCount, 0);
        }
    }

    internal static bool Evaluate(string path, Func<string, bool> evaluate)
    {
        var fullPath = Path.GetFullPath(path);
        if (!TryReadStamp(fullPath, out var stamp))
        {
            return false;
        }

        if (Entries.TryGetValue(stamp, out var cached))
        {
            // A sharing violation or access change must still fail, even with an unchanged stamp.
            using var readable = File.OpenRead(fullPath);
            return TryReadStamp(fullPath, out var current) && current == stamp && cached;
        }

        Interlocked.Increment(ref loadCount);
        bool verdict;
        try
        {
            verdict = evaluate(fullPath);
        }
        catch (XmlException)
        {
            // Malformed content is stable under the stamp; transient access/IO failures are not.
            verdict = false;
        }
        if (!TryReadStamp(fullPath, out var afterParse) || afterParse != stamp)
        {
            return false;
        }

        lock (InsertionLock)
        {
            // Serialize only insertions, so concurrent misses cannot overrun the ceiling.
            if (Entries.Count >= MaxEntries)
            {
                Entries.Clear();
            }
            Entries[stamp] = verdict;
        }
        return verdict;
    }

    private static bool TryReadStamp(string fullPath, out FileStamp stamp)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            stamp = default;
            return false;
        }
        stamp = new FileStamp(fullPath, info.Length, info.LastWriteTimeUtc.Ticks);
        return true;
    }

    private readonly record struct FileStamp(string Path, long Length, long LastWriteTimeUtcTicks);
}
