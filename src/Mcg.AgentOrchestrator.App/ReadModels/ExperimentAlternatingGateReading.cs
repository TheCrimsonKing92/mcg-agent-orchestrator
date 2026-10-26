using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal static class ExperimentAlternatingGateReading
{
    internal static IReadOnlyDictionary<string, (double? Before, double? After)> Compute(
        string experimentId, ExperimentHostLedgers hostLedgers, DateTimeOffset comparisonStart, DateTimeOffset asOf)
    {
        bool Matches(DateTimeOffset at, IReadOnlyDictionary<string, string>? arms, string arm) =>
            at >= comparisonStart && at < asOf && arms is not null &&
            arms.TryGetValue(experimentId, out var recordedArm) && recordedArm == arm;

        double[] Gates(string arm) => SelectedGates(experimentId, hostLedgers, comparisonStart, asOf, arm)
            .Select(row => row.TotalMs / 1000).Order().ToArray();
        double[] Ticks(string arm) => hostLedgers.TickCpu.Where(row =>
                Matches(row.ObservedAt, row.ExperimentArms, arm))
            .Select(row => (double)row.CpuMs).ToArray();

        var offGates = Gates("off");
        var onGates = Gates("on");
        var values = new Dictionary<string, (double? Before, double? After)>(StringComparer.Ordinal)
        {
            ["gate-seconds"] = (null, null),
            ["tick-cpu-ms"] = (null, null)
        };
        if (offGates.Length < 3 || onGates.Length < 3) return values;
        values["gate-seconds"] = (Median(offGates), Median(onGates));
        var offTicks = Ticks("off");
        var onTicks = Ticks("on");
        if (offTicks.Length >= 3 && onTicks.Length >= 3)
            values["tick-cpu-ms"] = (offTicks.Average(), onTicks.Average());
        return values;
    }

    internal static IEnumerable<GateLaneTimeLedgerRecord> SelectedGates(string experimentId,
        ExperimentHostLedgers ledgers, DateTimeOffset since, DateTimeOffset until, string arm) =>
        ledgers.GatesInWindow(since, until).Where(row =>
            ExclusionReason(row, experimentId) is null && ResolveArm(row, experimentId) == arm);

    internal static string? ResolveArm(GateLaneTimeLedgerRecord row, string experimentId) =>
        row.ExperimentArms is not null && row.ExperimentArms.TryGetValue(experimentId, out var arm) ? arm : null;

    internal static string? ExclusionReason(GateLaneTimeLedgerRecord row, string experimentId) =>
        !string.Equals(row.Outcome, "completed", StringComparison.OrdinalIgnoreCase) ? $"outcome-{row.Outcome}" :
        ResolveArm(row, experimentId) is not ("off" or "on") ? "no-arm-recorded" : null;

    internal static double Median(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
