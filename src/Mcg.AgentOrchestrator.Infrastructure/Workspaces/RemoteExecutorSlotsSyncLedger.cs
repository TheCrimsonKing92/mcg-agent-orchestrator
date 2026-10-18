using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteExecutorSlotsSyncRow(DateTimeOffset ObservedAt, string ExecutorId, string IntentId,
    int Slots, string Outcome, string? Reason, string? RemoteBackup, string? ErrorTail);

internal static class RemoteExecutorSlotsSyncLedger
{
    internal const string FileName = "remote-executor-slots-sync.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static void Append(string path, RemoteExecutorSlotsSyncRow row)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SharedJsonlFile.AppendLine(path, JsonSerializer.Serialize(row, JsonOptions));
    }

    internal static IReadOnlyDictionary<string, RemoteExecutorSlotsSyncRow> ReadLatestByExecutor(string path)
    {
        var latest = new Dictionary<string, RemoteExecutorSlotsSyncRow>(StringComparer.Ordinal);
        if (!File.Exists(path)) return latest;
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                var row = JsonSerializer.Deserialize<RemoteExecutorSlotsSyncRow>(line, JsonOptions);
                if (row is not null && !string.IsNullOrWhiteSpace(row.ExecutorId) && row.Outcome is "synced" or "failed")
                    latest[row.ExecutorId] = row;
            }
            catch (JsonException) { }
        }
        return latest;
    }
}
