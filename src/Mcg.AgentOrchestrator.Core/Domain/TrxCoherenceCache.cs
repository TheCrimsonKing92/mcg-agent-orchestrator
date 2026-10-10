using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml;

namespace Mcg.AgentOrchestrator.Core;

// Owns only the per-file coherence verdict; receipt content hashes remain uncached.
internal static class TrxCoherenceCache
{
    private const int MaxEntries = 2048;
    private static readonly ConcurrentDictionary<ContentIdentity, bool> Entries = new();
    private static readonly object InsertionLock = new();
    private static long loadCount;
    private static int storeLoadClaimed;
    private static Action<PersistedVerdict>? verdictSink;

    internal static long LoadCount => Interlocked.Read(ref loadCount);

    internal static bool TryClaimStoreLoad() => Interlocked.CompareExchange(ref storeLoadClaimed, 1, 0) == 0;

    internal static void SetVerdictSink(Action<PersistedVerdict>? sink) => Volatile.Write(ref verdictSink, sink);

    internal static void Seed(PersistedVerdict entry)
    {
        var identity = new ContentIdentity(new FileStamp(entry.Path, entry.Length, entry.Ticks), entry.Sha256);
        lock (InsertionLock)
        {
            // Loading never evicts; duplicate keys still take the last verdict in file order.
            if (Entries.ContainsKey(identity) || Entries.Count < MaxEntries)
            {
                Entries[identity] = entry.Verdict;
            }
        }
    }

    internal static void Reset()
    {
        lock (InsertionLock)
        {
            Entries.Clear();
            Interlocked.Exchange(ref loadCount, 0);
            Interlocked.Exchange(ref storeLoadClaimed, 0);
            SetVerdictSink(null);
        }
    }

    internal static bool Evaluate(string path, Func<string, bool> evaluate)
    {
        var fullPath = Path.GetFullPath(path);
        if (!TryReadStamp(fullPath, out var stamp))
        {
            return false;
        }

        // Keep the handle open through evaluation: Windows denies writes and replacement while
        // the hash and the parser read the same file. A stamp alone cannot detect restored timestamps.
        using var readable = File.OpenRead(fullPath);
        var identity = new ContentIdentity(stamp, Convert.ToHexString(SHA256.HashData(readable)));
        if (Entries.TryGetValue(identity, out var cached))
        {
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
            // Malformed content is stable under its hash; transient access/IO failures are not.
            verdict = false;
        }
        if (!TryReadStamp(fullPath, out var afterParse) || afterParse != stamp)
        {
            return false;
        }

        Action<PersistedVerdict>? sink;
        lock (InsertionLock)
        {
            // Serialize only insertions, so concurrent misses cannot overrun the ceiling.
            if (Entries.Count >= MaxEntries)
            {
                Entries.Clear();
            }
            Entries[identity] = verdict;
            sink = Volatile.Read(ref verdictSink);
        }
        try
        {
            // Persistence runs outside the insertion lock and cannot replace the computed verdict.
            sink?.Invoke(new PersistedVerdict(fullPath, stamp.Length, stamp.LastWriteTimeUtcTicks, identity.Sha256, verdict));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
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
    private readonly record struct ContentIdentity(FileStamp Stamp, string Sha256);
    internal readonly record struct PersistedVerdict(string Path, long Length, long Ticks, string Sha256, bool Verdict);
}
