using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal sealed record ExperimentMetricReading(string Metric, double? Before, double? After);

internal sealed record ExperimentReadingResult(int? ObservedCount, ExperimentStopRule StopRule,
    bool StopRuleMet, IReadOnlyList<ExperimentMetricReading> Metrics, string Verdict, string Reason,
    bool GuardrailBreached, string? Goals = null, string? GoalsLabel = null, int? StopTarget = null,
    string? ObservedBasis = null);

internal static class ExperimentGoals
{
    internal static IReadOnlyCollection<Goal> Read(string statePath)
    {
        if (!File.Exists(statePath)) return [];
        var queries = SqliteOrchestratorStateRepository.OpenReadOnly(statePath);
        var metadata = queries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        return metadata.Count == 0 ? [] : queries.LoadGoalsAsync(
            metadata.Select(goal => new GoalId(goal.Id)).ToArray()).GetAwaiter().GetResult().Goals;
    }
}

internal static class ExperimentReading
{
    internal static ExperimentReadingResult Evaluate(ExperimentRecord record, IReadOnlyCollection<Goal> goals,
        IReadOnlyDictionary<string, DateTimeOffset> landings, IReadOnlyCollection<AppliedRetryIntent> intents,
        DateTimeOffset asOf, int? observedGateCount, ExperimentHostLedgers hostLedgers)
    {
        var spec = record.Spec;
        var start = ComparisonStart(record);
        // Ticks still have no authoritative queryable counter.
        var incremental = spec.StopRule.Unit == ExperimentStopUnit.Goals &&
            spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort && spec.Baseline.IncrementalMembers == true;
        var observedBasis = incremental ? "landed-member-goals" : null;
        var observed = incremental ? ExperimentIncrementalStopCount.Count(record, goals) : spec.StopRule.Unit == ExperimentStopUnit.Goals
            ? goals.Count(g => g.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed &&
                g.Tasks.SelectMany(t => t.DispatchHistory).Select(d => (DateTimeOffset?)d.DispatchedAt)
                    .Max() is { } at && at >= start && at < asOf)
            : spec.StopRule.Unit == ExperimentStopUnit.Gates ? observedGateCount : (int?)null;
        var target = ExperimentStopTarget.Effective(spec.StopRule);
        var stopRuleMet = observed is { } n && n >= target;
        if (spec.Baseline.Kind == ExperimentBaselineKind.AlternatingGates &&
            !spec.Metrics.Append(spec.Guardrail.Metric)
                .Concat(spec.DecisionRule.KeepIf.Concat(spec.DecisionRule.RevertIf)
                    .Append(spec.Guardrail.BreachIf).Select(condition => condition.Metric))
                .All(metric => metric is "gate-seconds" or "tick-cpu-ms"))
        {
            return new(observed, spec.StopRule, stopRuleMet, [], "unavailable",
                $"baseline kind {Name(spec.Baseline.Kind)} not computable in this slice", false, StopTarget: target);
        }

        IReadOnlyDictionary<string, (double? Before, double? After)> values;
        ExperimentTwinValues? twin = null;
        if (spec.Baseline.Kind == ExperimentBaselineKind.AlternatingGates)
        {
            values = ExperimentAlternatingGateReading.Compute(record.Id, hostLedgers, start, asOf);
        }
        else if (spec.Baseline.Kind == ExperimentBaselineKind.TwinGoal)
        {
            twin = ExperimentTwinReading.Compute(spec.Baseline, goals, intents, landings);
            values = twin.Values;
        }
        else if (spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort)
        {
            twin = ExperimentCohortReading.Compute(spec.Baseline, goals, intents, landings);
            values = twin.Values;
        }
        else
        {
            var since = spec.Baseline.Since!.Value;
            var until = spec.Baseline.Until!.Value;
            var baseline = RoundValueReport.Build(goals, since, until, intents).Window;
            var comparison = asOf > until ? RoundValueReport.Build(goals, until, asOf, intents).Window : null;
            values = ExperimentMetrics.Menu.ToDictionary(metric => metric, metric => (
                Before: metric == "gate-seconds" ? hostLedgers.GateSeconds(since, until, spec.Baseline.MinGateSeconds) :
                    Value(metric, baseline, goals, landings, since, until, hostLedgers),
                After: metric == "gate-seconds" ? hostLedgers.GateSeconds(until, asOf, spec.Baseline.MinGateSeconds) :
                    Value(metric, comparison, goals, landings, until, asOf, hostLedgers)));
        }
        var metrics = spec.Metrics.Append(spec.Guardrail.Metric).Select(metric =>
        {
            var value = values[metric];
            return new ExperimentMetricReading(metric, value.Before, value.After);
        }).ToArray();

        bool? EvaluateCondition(ExperimentCondition condition)
        {
            var (before, after) = values[condition.Metric];
            if (before is null or 0 || after is null) return null;
            var change = (after.Value - before.Value) / before.Value * 100;
            return condition.Op switch
            {
                "<" => change < condition.ChangePercent, "<=" => change <= condition.ChangePercent,
                ">" => change > condition.ChangePercent, ">=" => change >= condition.ChangePercent,
                _ => throw new InvalidDataException("Stored experiment condition has an invalid operator.")
            };
        }

        var keep = spec.DecisionRule.KeepIf.Select(EvaluateCondition).ToArray();
        var revert = spec.DecisionRule.RevertIf.Select(EvaluateCondition).ToArray();
        var guardrail = EvaluateCondition(spec.Guardrail.BreachIf);
        if (keep.Contains(null) || revert.Contains(null) || guardrail is null)
        {
            var missing = spec.DecisionRule.KeepIf.Concat(spec.DecisionRule.RevertIf)
                .Append(spec.Guardrail.BreachIf).Where(c => EvaluateCondition(c) is null).Select(c => c.Metric).Distinct();
            return new(observed, spec.StopRule, stopRuleMet, metrics, "inconclusive",
                $"unavailable comparison or zero baseline: {string.Join(", ", missing)}" +
                (twin is { Diagnostics.Count: > 0 } ? $"; {string.Join("; ", twin.Diagnostics)}" : ""),
                guardrail == true, twin?.Goals, spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort ? "cohort goals" : null, target, observedBasis);
        }
        var keepHolds = keep.All(v => v == true);
        var revertHolds = revert.All(v => v == true);
        var verdict = keepHolds && revertHolds ? "inconclusive" : revertHolds ? "revert" :
            keepHolds && guardrail == false ? "keep" : "inconclusive";
        var reason = keepHolds && revertHolds ? "conflicting keep and revert rules" : "pre-registered rules evaluated";
        if (guardrail == true)
        {
            var value = values[spec.Guardrail.Metric];
            reason += $"; guardrail breached: {spec.Guardrail.Metric} baseline={Format(value.Before)} comparison={Format(value.After)}";
        }
        return new(observed, spec.StopRule, stopRuleMet, metrics, verdict, reason, guardrail == true, twin?.Goals,
            spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort ? "cohort goals" : null, target, observedBasis);
    }

    internal static DateTimeOffset ComparisonStart(ExperimentRecord record) => record.Spec.Baseline.Until ?? record.CreatedAt;

    internal static bool IsWindowMetric(string metric) => metric is "landings-per-hour" or "gate-seconds" or "tick-cpu-ms";

    internal static void Render(ExperimentReadingResult result)
    {
        var target = result.StopTarget ?? ExperimentStopTarget.Effective(result.StopRule);
        var progress = result.ObservedCount is { } count ? $"{count} of {target}" : $"unavailable of {target}";
        var met = result.ObservedCount is not null ? result.StopRuleMet ? "met" : "not met" : "progress unavailable";
        Console.WriteLine($"stop rule: {progress} {Name(result.StopRule.Unit)} ({met})");
        if (result.ObservedBasis is not null) Console.WriteLine("counted: landed member goals, both arms");
        if (result.Goals is not null) Console.WriteLine($"{result.GoalsLabel ?? "twin goals"}: {result.Goals}");
        foreach (var metric in result.Metrics)
            Console.WriteLine($"{metric.Metric.Replace('-', ' ')}: baseline={Format(metric.Before)} comparison={Format(metric.After)}");
        Console.WriteLine($"reading: {result.Verdict} ({result.Reason})");
    }

    private static double? Value(string metric, RoundValueTotals? totals, IReadOnlyCollection<Goal> goals,
        IReadOnlyDictionary<string, DateTimeOffset> landings, DateTimeOffset since, DateTimeOffset until,
        ExperimentHostLedgers hostLedgers)
    {
        if (metric == "hours-to-land") return null;
        if (metric == "landings-per-hour")
        {
            var count = goals.Count(g => g.Status == GoalStatus.Completed && landings.TryGetValue(g.Id.Value, out var at)
                && at >= since && at < until);
            return until <= since || count == 0 ? null : count / (until - since).TotalHours;
        }
        if (metric == "gate-seconds") return hostLedgers.GateSeconds(since, until);
        if (metric == "tick-cpu-ms") return hostLedgers.TickCpuMs(since, until);
        return TotalsValue(metric, totals);
    }

    internal static double? TotalsValue(string metric, RoundValueTotals? totals)
    {
        if (totals is null || totals.LandedGoals == 0) return null;
        return metric switch
        {
            "rounds-per-landing" => totals.RoundsPerLanding,
            "productive-rounds" => totals.Productive,
            "expected-overhead-rounds" => totals.ExpectedOverhead,
            "wasted-rounds" => totals.Wasted,
            _ => throw new InvalidDataException($"Unknown stored experiment metric '{metric}'.")
        };
    }

    internal static string Name<T>(T value) where T : Enum => System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
    private static string Format(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "unavailable";
}

internal static class ExperimentLandingTimes
{
    internal static IReadOnlyDictionary<string, DateTimeOffset> Read(string directory)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("eventType", out var kind) || kind.GetString() != "GoalLanded") continue;
                var id = root.GetProperty("goalId").GetString();
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (id is not null && (!result.TryGetValue(id, out var previous) || at < previous)) result[id] = at;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return result;
    }
}
