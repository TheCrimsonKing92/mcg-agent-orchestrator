using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ExperimentOverlap
{
    internal sealed record Result(ExperimentRecord Other, IReadOnlyList<string> SharedMetrics);

    internal static IReadOnlyList<Result> Find(ExperimentRecord subject, IEnumerable<ExperimentRecord> candidates)
    {
        var start = subject.Spec.Baseline.Until ?? subject.CreatedAt;
        var end = subject.Decision?.DecidedAt;
        if (end is not null && end <= start) return [];
        var metrics = subject.Spec.Metrics.Append(subject.Spec.Guardrail.Metric).ToHashSet(StringComparer.Ordinal);
        var overlaps = new List<Result>();
        foreach (var other in candidates)
        {
            if (StringComparer.Ordinal.Equals(subject.Id, other.Id)) continue;
            var otherStart = other.Spec.Baseline.Until ?? other.CreatedAt;
            var otherEnd = other.Decision?.DecidedAt;
            if (otherEnd is not null && (otherEnd <= otherStart || start >= otherEnd)) continue;
            if (end is not null && otherStart >= end) continue;
            var shared = other.Spec.Metrics.Append(other.Spec.Guardrail.Metric)
                .Intersect(metrics, StringComparer.Ordinal).OrderBy(metric => metric, StringComparer.Ordinal).ToArray();
            if (shared.Length > 0) overlaps.Add(new(other, shared));
        }
        return overlaps.OrderBy(overlap => overlap.Other.CreatedAt)
            .ThenBy(overlap => overlap.Other.Id, StringComparer.Ordinal).ToArray();
    }
}
