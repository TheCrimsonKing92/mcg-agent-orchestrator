using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record GateLaneTimeLedgerRecord(
    DateTimeOffset ObservedAt, string GateAttemptId, string? GoalId, string Outcome,
    double TotalMs, double LocalLaneMs, double RemoteLaneMs, int RemoteLaneCount, int Version = 1);

internal static class GateLaneTimeLedger
{
    internal const string FileName = "gate-lane-time.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<Action<string, string>?> AppenderOverride = new();

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator", FileName);

    internal static void Append(string path, GateLaneTimeLedgerRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        (AppenderOverride.Value ?? SharedJsonlFile.AppendLine)(path, JsonSerializer.Serialize(record, JsonOptions));
    }

    internal static IDisposable PushAppender(Action<string, string> appender)
    {
        ArgumentNullException.ThrowIfNull(appender);
        var previous = AppenderOverride.Value;
        AppenderOverride.Value = appender;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(Action<string, string>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AppenderOverride.Value = previous;
        }
    }
}
