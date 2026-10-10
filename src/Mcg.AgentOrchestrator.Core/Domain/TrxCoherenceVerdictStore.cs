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
                foreach (var line in SharedJsonlFile.ReadLines(Path.Combine(stateDirectory, FileName)))
                {
                    var record = TryDeserialize(line);
                    if (record is null || record.Version != CurrentVersion ||
                        string.IsNullOrWhiteSpace(record.Path) || !Path.IsPathFullyQualified(record.Path) ||
                        record.Length < 0 || record.Ticks < 0 || record.Ticks > DateTime.MaxValue.Ticks ||
                        record.Sha256 is not { Length: 64 } || !record.Sha256.All(Uri.IsHexDigit))
                    {
                        continue;
                    }

                    TrxCoherenceCache.Seed(new TrxCoherenceCache.PersistedVerdict(
                        record.Path, record.Length, record.Ticks, record.Sha256.ToUpperInvariant(), record.Verdict));
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
                Directory.CreateDirectory(stateDirectory);
                SharedJsonlFile.AppendLine(Path.Combine(stateDirectory, FileName), JsonSerializer.Serialize(
                    new VerdictRecord(CurrentVersion, entry.Path, entry.Length, entry.Ticks, entry.Sha256, entry.Verdict),
                    JsonOptions));
            }
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            // A failed append must never alter a coherence verdict.
        }
    }

    private static VerdictRecord? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize<VerdictRecord>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool IsNonFatalStoreFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed record VerdictRecord(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string Path,
        [property: JsonRequired] long Length,
        [property: JsonRequired] long Ticks,
        [property: JsonRequired] string Sha256,
        [property: JsonRequired] bool Verdict);
}
