using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ExperimentHostLedgers(
    IReadOnlyList<GateLaneTimeLedgerRecord> GateLaneTimes, IReadOnlyList<TickCpuLedgerRecord> TickCpu)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static ExperimentHostLedgers Empty { get; } = new([], []);

    internal static ExperimentHostLedgers Read(string executionDirectory) => new(
        ReadRecords<GateLaneTimeLedgerRecord>(GateLaneTimeLedger.ResolveStorePath(executionDirectory)),
        ReadRecords<TickCpuLedgerRecord>(TickCpuLedger.ResolveStorePath(executionDirectory)));

    internal double? GateSeconds(DateTimeOffset since, DateTimeOffset until, double? minSeconds = null)
    {
        var values = CompletedGates(since, until, minSeconds)
            .Select(record => record.TotalMs / 1000).Order().ToArray();
        if (values.Length == 0) return null;
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    internal int CompletedGateCount(DateTimeOffset since, DateTimeOffset until, double? minSeconds = null) =>
        CompletedGates(since, until, minSeconds).Count();

    internal IEnumerable<GateLaneTimeLedgerRecord> CompletedGates(
        DateTimeOffset since, DateTimeOffset until, double? minSeconds) =>
        GatesInWindow(since, until).Where(record => ExclusionReason(record, minSeconds) is null);

    internal IEnumerable<GateLaneTimeLedgerRecord> GatesInWindow(DateTimeOffset since, DateTimeOffset until) =>
        GateLaneTimes.Where(record => record.ObservedAt >= since && record.ObservedAt < until);

    internal static string? ExclusionReason(GateLaneTimeLedgerRecord record, double? minSeconds) =>
        !string.Equals(record.Outcome, "completed", StringComparison.OrdinalIgnoreCase) ? $"outcome-{record.Outcome}" :
        !(minSeconds is null || record.TotalMs / 1000.0 >= minSeconds.Value) ? "below-min-gate-seconds" : null;

    internal double? TickCpuMs(DateTimeOffset since, DateTimeOffset until)
    {
        var values = TickCpu.Where(record => record.ObservedAt >= since && record.ObservedAt < until)
            .Select(record => (double)record.CpuMs).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static IReadOnlyList<T> ReadRecords<T>(string path) where T : class
    {
        var result = new List<T>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(line, JsonOptions) is { } record) result.Add(record);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException or FormatException) { }
        }
        return result;
    }
}
