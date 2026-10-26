using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal static class ExperimentGateStopCount
{
    internal static int Count(ExperimentRecord record, DateTimeOffset asOf, ExperimentHostLedgers ledgers,
        Func<IReadOnlyCollection<DateTimeOffset>> conductAttempts)
    {
        var since = ExperimentReading.ComparisonStart(record);
        var usesGateSeconds = record.Spec.Metrics?.Any(metric =>
            string.Equals(metric, "gate-seconds", StringComparison.OrdinalIgnoreCase)) == true ||
            string.Equals(record.Spec.Guardrail?.Metric, "gate-seconds", StringComparison.OrdinalIgnoreCase);
        return usesGateSeconds
            ? ledgers.CompletedGateCount(since, asOf, record.Spec.Baseline.MinGateSeconds)
            : ExperimentGateAttempts.Count(conductAttempts(), since, asOf);
    }
}
