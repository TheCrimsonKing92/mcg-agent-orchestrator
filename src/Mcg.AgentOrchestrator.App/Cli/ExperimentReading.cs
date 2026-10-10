using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ExperimentMetricReading(string Metric, double? Before, double? After);

internal sealed record ExperimentReadingResult(int? ObservedCount, ExperimentStopRule StopRule,
    bool StopRuleMet, IReadOnlyList<ExperimentMetricReading> Metrics, string Verdict, string Reason,
    bool GuardrailBreached, string? Goals = null, string? GoalsLabel = null);

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
        DateTimeOffset asOf, int? observedGateCount)
    {
        var spec = record.Spec;
        var start = ComparisonStart(record);
        // Ticks still have no authoritative queryable counter.
        var observed = spec.StopRule.Unit == ExperimentStopUnit.Goals
            ? goals.Count(g => g.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed &&
                g.Tasks.SelectMany(t => t.DispatchHistory).Select(d => (DateTimeOffset?)d.DispatchedAt)
                    .Max() is { } at && at >= start && at < asOf)
            : spec.StopRule.Unit == ExperimentStopUnit.Gates ? observedGateCount : (int?)null;
        var stopRuleMet = observed is { } n && n >= spec.StopRule.Count;
        if (spec.Baseline.Kind == ExperimentBaselineKind.AlternatingGates)
        {
            return new(observed, spec.StopRule, stopRuleMet, [], "unavailable",
                $"baseline kind {Name(spec.Baseline.Kind)} not computable in this slice", false);
        }

        IReadOnlyDictionary<string, (double? Before, double? After)> values;
        ExperimentTwinValues? twin = null;
        if (spec.Baseline.Kind == ExperimentBaselineKind.TwinGoal)
        {
            twin = ExperimentTwinReading.Compute(spec.Baseline, goals, intents);
            values = twin.Values;
        }
        else if (spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort)
        {
            twin = ExperimentCohortReading.Compute(spec.Baseline, goals, intents);
            values = twin.Values;
        }
        else
        {
            var since = spec.Baseline.Since!.Value;
            var until = spec.Baseline.Until!.Value;
            var baseline = RoundValueReport.Build(goals, since, until, intents).Window;
            var comparison = asOf > until ? RoundValueReport.Build(goals, until, asOf, intents).Window : null;
            values = ExperimentMetrics.Menu.ToDictionary(metric => metric, metric => (
                Before: Value(metric, baseline, goals, landings, since, until),
                After: Value(metric, comparison, goals, landings, until, asOf)));
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
                guardrail == true, twin?.Goals, spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort ? "cohort goals" : null);
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
            spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort ? "cohort goals" : null);
    }

    internal static DateTimeOffset ComparisonStart(ExperimentRecord record) => record.Spec.Baseline.Until ?? record.CreatedAt;

    internal static void Render(ExperimentReadingResult result)
    {
        var progress = result.ObservedCount is { } count ? $"{count} of {result.StopRule.Count}" : $"unavailable of {result.StopRule.Count}";
        var met = result.ObservedCount is not null ? result.StopRuleMet ? "met" : "not met" : "progress unavailable";
        Console.WriteLine($"stop rule: {progress} {Name(result.StopRule.Unit)} ({met})");
        if (result.Goals is not null) Console.WriteLine($"{result.GoalsLabel ?? "twin goals"}: {result.Goals}");
        foreach (var metric in result.Metrics)
            Console.WriteLine($"{metric.Metric.Replace('-', ' ')}: baseline={Format(metric.Before)} comparison={Format(metric.After)}");
        Console.WriteLine($"reading: {result.Verdict} ({result.Reason})");
    }

    private static double? Value(string metric, RoundValueTotals? totals, IReadOnlyCollection<Goal> goals,
        IReadOnlyDictionary<string, DateTimeOffset> landings, DateTimeOffset since, DateTimeOffset until)
    {
        if (metric == "landings-per-hour")
        {
            var count = goals.Count(g => g.Status == GoalStatus.Completed && landings.TryGetValue(g.Id.Value, out var at)
                && at >= since && at < until);
            return until <= since || count == 0 ? null : count / (until - since).TotalHours;
        }
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
