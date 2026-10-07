using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record HostHealthLedgerRecord(
    DateTimeOffset ObservedAt, string GateAttemptId, double LaunchMs, double? PagedPoolMb, int Version = 1,
    double? FileCachePagedPoolMb = null);

internal static class GateHostHealthLedger
{
    internal const string FileName = "host-health.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", FileName);

    internal static void Append(string path, HostHealthLedgerRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(record, JsonOptions));
    }

    // I/O failures remain distinguishable from a genuinely healthy ledger. The caller owns best-effort handling.
    internal static IReadOnlyList<HostHealthLedgerRecord> ReadRecent(string path)
    {
        var records = new Queue<HostHealthLedgerRecord>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            HostHealthLedgerRecord? record;
            try { record = JsonSerializer.Deserialize<HostHealthLedgerRecord>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (record is null || record.Version != 1 || string.IsNullOrWhiteSpace(record.GateAttemptId) ||
                !double.IsFinite(record.LaunchMs) || record.LaunchMs < 0 ||
                record.PagedPoolMb is { } pool && (!double.IsFinite(pool) || pool < 0))
                continue;
            records.Enqueue(record);
            if (records.Count > GateHostHealthEvaluator.RetainedRecordWindow)
                records.Dequeue();
        }
        return records.ToArray();
    }
}
