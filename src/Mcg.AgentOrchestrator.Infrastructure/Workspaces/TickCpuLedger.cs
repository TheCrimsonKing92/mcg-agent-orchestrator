using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TickCpuLedgerRecord(
    DateTimeOffset ObservedAt, int Tick, long CpuMs, long CpuSweepMs, long CpuPrewalkMs,
    long CpuWalkMs, bool HadGoalChanges, int Version = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? ExperimentArms = null);

internal static class TickCpuLedger
{
    internal const string FileName = "tick-cpu.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AsyncLocal<Action<string, string>?> AppenderOverride = new();

    internal static string ResolveStorePath(string worktreePath) => Path.Combine(
        HostLedgerStateDirectory.Resolve(worktreePath), FileName);

    internal static string ResolveStorePath(string worktreePath, Func<string, string>? route) => Path.Combine(
        HostLedgerStateDirectory.Resolve(worktreePath, route), FileName);

    internal static void TryAppend(string? executionDirectory, int tick, long cpuMs,
        long cpuSweepMs, long cpuPrewalkMs, long cpuWalkMs, bool hadGoalChanges)
    {
        if (executionDirectory is null) return;
        try
        {
            var path = ResolveStorePath(executionDirectory);
            var arms = ExperimentArmStamp.Resolve(executionDirectory);
            var record = new TickCpuLedgerRecord(DateTimeOffset.UtcNow, tick, cpuMs,
                cpuSweepMs, cpuPrewalkMs, cpuWalkMs, hadGoalChanges,
                Version: arms is { Count: > 0 } ? 2 : 1, ExperimentArms: arms);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            (AppenderOverride.Value ?? SharedJsonlFile.AppendLine)(path, JsonSerializer.Serialize(record, JsonOptions));
        }
        catch { /* Telemetry cannot fault a tick or change its progress output. */ }
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
