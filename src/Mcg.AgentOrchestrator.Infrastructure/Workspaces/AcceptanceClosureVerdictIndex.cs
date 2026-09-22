using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceClosureVerdictRecord(
    int Version,
    string ManifestIdentity,
    string PartitionFilterHash,
    string ClosureHash,
    string SourceAttemptId,
    IReadOnlyList<string> TestResultPaths,
    DateTimeOffset RecordedAt);

internal sealed class AcceptanceClosureVerdictIndex
{
    private const int CurrentVersion = 1;
    private const string FileName = "acceptance-closure-verdicts.jsonl";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    internal AcceptanceClosureVerdictIndex(string worktreePath) =>
        _path = Path.Combine(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
            ".orchestrator",
            FileName);

    internal AcceptanceClosureVerdictRecord? FindLatest(
        string manifestIdentity,
        string filterHash,
        string closureHash)
    {
        try
        {
            lock (Gate)
            {
                return SharedJsonlFile.ReadAllLines(_path)
                    .Select(TryDeserialize)
                    .Where(record =>
                        record is not null &&
                        record.Version == CurrentVersion &&
                        record.ManifestIdentity.Equals(manifestIdentity, StringComparison.Ordinal) &&
                        record.PartitionFilterHash.Equals(filterHash, StringComparison.OrdinalIgnoreCase) &&
                        record.ClosureHash.Equals(closureHash, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(record => record!.RecordedAt)
                    .LastOrDefault();
            }
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            return null;
        }
    }

    internal void AppendGreen(
        string manifestIdentity,
        string filterHash,
        string closureHash,
        string sourceAttemptId,
        IReadOnlyList<string> testResultPaths)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                SharedJsonlFile.AppendLines(
                    _path,
                    [JsonSerializer.Serialize(
                        new AcceptanceClosureVerdictRecord(
                            CurrentVersion,
                            manifestIdentity,
                            filterHash,
                            closureHash,
                            sourceAttemptId,
                            testResultPaths,
                            DateTimeOffset.UtcNow),
                        JsonOptions)]);
            }
        }
        catch (Exception exception) when (IsNonFatalStoreFailure(exception))
        {
            // Cache persistence is advisory and must never replace the gate verdict.
        }
    }

    private static AcceptanceClosureVerdictRecord? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize<AcceptanceClosureVerdictRecord>(line, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool IsNonFatalStoreFailure(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
