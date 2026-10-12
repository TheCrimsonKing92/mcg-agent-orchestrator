using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ExperimentGateAudit
{
    internal static IReadOnlyList<string> Lines(ExperimentRecord record, ExperimentHostLedgers ledgers, DateTimeOffset asOf)
    {
        var usesGateSeconds = record.Spec.Metrics?.Any(metric =>
            string.Equals(metric, "gate-seconds", StringComparison.OrdinalIgnoreCase)) == true ||
            string.Equals(record.Spec.Guardrail?.Metric, "gate-seconds", StringComparison.OrdinalIgnoreCase);
        if (!usesGateSeconds || record.Spec.Baseline.Kind is not
            (ExperimentBaselineKind.AlternatingGates or ExperimentBaselineKind.BeforeAfterWindow)) return [];

        var lines = new List<string> { "gate audit:" };
        if (record.Spec.Baseline.Kind == ExperimentBaselineKind.AlternatingGates)
        {
            var since = ExperimentReading.ComparisonStart(record);
            var rows = ledgers.GatesInWindow(since, asOf).Where(row =>
                row.ExperimentArms is null || row.ExperimentArms.Count == 0 || row.ExperimentArms.ContainsKey(record.Id)).ToArray();
            foreach (var arm in new[] { "off", "on" })
            {
                var counted = ExperimentAlternatingGateReading.SelectedGates(record.Id, ledgers, since, asOf, arm).ToArray();
                var seconds = counted.Select(row => row.TotalMs / 1000).Order().ToArray();
                var excluded = rows.Where(row =>
                    ExperimentAlternatingGateReading.ResolveArm(row, record.Id) == arm ||
                    arm == "on" && ExperimentAlternatingGateReading.ResolveArm(row, record.Id) is not ("off" or "on"));
                AddBlock(lines, $"arm {arm}", counted,
                    seconds.Length == 0 ? null : ExperimentAlternatingGateReading.Median(seconds), excluded,
                    row => ExperimentAlternatingGateReading.ExclusionReason(row, record.Id));
            }
        }
        else
        {
            var baseline = record.Spec.Baseline;
            AddWindow("baseline window", baseline.Since!.Value, baseline.Until!.Value);
            AddWindow("comparison window", baseline.Until!.Value, asOf);

            void AddWindow(string group, DateTimeOffset since, DateTimeOffset until) =>
                AddBlock(lines, group, ledgers.CompletedGates(since, until, baseline.MinGateSeconds).ToArray(),
                    ledgers.GateSeconds(since, until, baseline.MinGateSeconds), ledgers.GatesInWindow(since, until),
                    row => ExperimentHostLedgers.ExclusionReason(row, baseline.MinGateSeconds));
        }
        return lines;
    }

    private static void AddBlock(List<string> lines, string group, IReadOnlyList<GateLaneTimeLedgerRecord> counted,
        double? median, IEnumerable<GateLaneTimeLedgerRecord> excluded, Func<GateLaneTimeLedgerRecord, string?> reason)
    {
        lines.Add($"  {group}: {counted.Count} gates, median {(median is { } value ? $"{Seconds(value)}s" : "none")}");
        foreach (var row in Ordered(counted))
            lines.Add($"    {row.GateAttemptId} {row.ObservedAt.ToString("O", CultureInfo.InvariantCulture)} {Seconds(row.TotalMs / 1000)}s");
        foreach (var row in Ordered(excluded))
            if (reason(row) is { } exclusion) lines.Add($"    excluded {row.GateAttemptId} {exclusion}");
    }

    private static IOrderedEnumerable<GateLaneTimeLedgerRecord> Ordered(IEnumerable<GateLaneTimeLedgerRecord> rows) =>
        rows.OrderBy(row => row.ObservedAt).ThenBy(row => row.GateAttemptId, StringComparer.Ordinal);

    private static string Seconds(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}
