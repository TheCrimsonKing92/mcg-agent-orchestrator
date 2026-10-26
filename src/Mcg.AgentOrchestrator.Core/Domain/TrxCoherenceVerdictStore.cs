using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

// Advisory persistence for hash-keyed coherence verdicts; receipt hashes remain uncached.
public static class TrxCoherenceVerdictStore
{
    // Bump whenever the coherence evaluator's decision semantics change.
    private const int CurrentVersion = 1;
    private const string FileName = "trx-coherence-verdicts.jsonl";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Dictionary<VerdictKey, bool>> Persisted = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static void Load(string stateDirectory)
    {
        if (!TrxCoherenceCache.TryClaimStoreLoad())
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var directoryKey = Path.GetFullPath(stateDirectory);
                var logPath = Path.Combine(directoryKey, FileName);
                var latest = new Dictionary<VerdictKey, (long Order, string Line, TrxCoherenceCache.PersistedVerdict Entry)>();
                long linesRead = 0;
                foreach (var line in SharedJsonlFile.ReadLines(logPath))
                {
                    linesRead++;
                    var record = TryDeserialize(line);
                    if (record is null || record.Version != CurrentVersion ||
                        string.IsNullOrWhiteSpace(record.Path) || !Path.IsPathFullyQualified(record.Path) ||
                        record.Length < 0 || record.Ticks < 0 || record.Ticks > DateTime.MaxValue.Ticks ||
                        record.Sha256 is not { Length: 64 } || !record.Sha256.All(Uri.IsHexDigit))
                    {
                        continue;
                    }

                    var entry = new TrxCoherenceCache.PersistedVerdict(
                        record.Path, record.Length, record.Ticks, record.Sha256.ToUpperInvariant(), record.Verdict);
                    latest[Key(entry)] = (linesRead, line, entry);
                }

                // Only a complete scan can establish the latest verdicts and newest identities.
                var kept = latest.Values.OrderBy(item => item.Order).TakeLast(TrxCoherenceCache.MaxEntries).ToArray();
                foreach (var item in kept)
                {
                    TrxCoherenceCache.Seed(item.Entry);
                }
                Persisted[directoryKey] = kept.ToDictionary(item => Key(item.Entry), item => item.Entry.Verdict);
                if (kept.Length < linesRead)
                {
                    TryCompact(logPath, kept.Select(item => item.Line));
                }
            }
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            // Missing or unusable persistence leaves evaluation to the live file and evaluator.
        }
    }

    public static void EnableAppend(string stateDirectory) =>
        TrxCoherenceCache.SetVerdictSink(entry => Append(stateDirectory, entry));

    private static void Append(string stateDirectory, TrxCoherenceCache.PersistedVerdict entry)
    {
        try
        {
            lock (Gate)
            {
                var directoryKey = Path.GetFullPath(stateDirectory);
                if (!Persisted.TryGetValue(directoryKey, out var persisted))
                {
                    persisted = new Dictionary<VerdictKey, bool>();
                    Persisted[directoryKey] = persisted;
                }
                var key = Key(entry);
                if (persisted.TryGetValue(key, out var verdict) && verdict == entry.Verdict)
                {
                    return;
                }
                Directory.CreateDirectory(stateDirectory);
                SharedJsonlFile.AppendLine(Path.Combine(directoryKey, FileName), JsonSerializer.Serialize(
                    new VerdictRecord(CurrentVersion, entry.Path, entry.Length, entry.Ticks, entry.Sha256, entry.Verdict),
                    JsonOptions));
                persisted[key] = entry.Verdict;
            }
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            // A failed append must never alter a coherence verdict.
        }
    }

    private static VerdictKey Key(TrxCoherenceCache.PersistedVerdict entry) =>
        new(entry.Path, entry.Length, entry.Ticks, entry.Sha256);

    private static void TryCompact(string logPath, IEnumerable<string> lines)
    {
        var temporaryPath = logPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporaryPath, lines);
            File.Move(temporaryPath, logPath, overwrite: true);
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            // The original log remains usable when writing or replacing its compacted copy fails.
            try { File.Delete(temporaryPath); }
            catch (Exception cleanupException) when (IsNonFatalStoreFailure(cleanupException)) { }
        }
    }

    private static VerdictRecord? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize<VerdictRecord>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool IsNonFatalStoreFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private readonly record struct VerdictKey(string Path, long Length, long Ticks, string Sha256);

    private sealed record VerdictRecord(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string Path,
        [property: JsonRequired] long Length,
        [property: JsonRequired] long Ticks,
        [property: JsonRequired] string Sha256,
        [property: JsonRequired] bool Verdict);
}
