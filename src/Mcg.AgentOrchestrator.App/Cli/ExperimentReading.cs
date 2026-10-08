using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ExperimentReading
{
    internal static void Write(ExperimentRecord record, IReadOnlyCollection<Goal> goals,
        IReadOnlyDictionary<string, DateTimeOffset> landings, IReadOnlyCollection<AppliedRetryIntent> intents,
        DateTimeOffset asOf)
    {
        var spec = record.Spec;
        var start = spec.Baseline.Until ?? record.CreatedAt;
        // Gates and ticks have no authoritative queryable counter in this slice.
        var observed = spec.StopRule.Unit == ExperimentStopUnit.Goals
            ? goals.Count(g => g.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded or GoalStatus.Failed &&
                g.Tasks.SelectMany(t => t.DispatchHistory).Select(d => (DateTimeOffset?)d.DispatchedAt)
                    .Max() is { } at && at >= start && at < asOf)
            : (int?)null;
        var progress = observed is { } count ? $"{count} of {spec.StopRule.Count}" : $"unavailable of {spec.StopRule.Count}";
        var met = observed is { } n ? n >= spec.StopRule.Count ? "met" : "not met" : "progress unavailable";
        Console.WriteLine($"stop rule: {progress} {Name(spec.StopRule.Unit)} ({met})");
        if (spec.Baseline.Kind != ExperimentBaselineKind.BeforeAfterWindow)
        {
            Console.WriteLine($"reading: unavailable (baseline kind {Name(spec.Baseline.Kind)} not computable in this slice)");
            return;
        }

        var since = spec.Baseline.Since!.Value;
        var until = spec.Baseline.Until!.Value;
        var baseline = RoundValueReport.Build(goals, since, until, intents).Window;
        var comparison = asOf > until ? RoundValueReport.Build(goals, until, asOf, intents).Window : null;
        var values = ExperimentMetrics.Menu.ToDictionary(metric => metric, metric => (
            Before: Value(metric, baseline, goals, landings, since, until),
            After: Value(metric, comparison, goals, landings, until, asOf)));
        foreach (var metric in spec.Metrics.Append(spec.Guardrail.Metric))
        {
            var value = values[metric];
            Console.WriteLine($"{metric.Replace('-', ' ')}: baseline={Format(value.Before)} comparison={Format(value.After)}");
        }

        bool? Evaluate(ExperimentCondition condition)
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

        var keep = spec.DecisionRule.KeepIf.Select(Evaluate).ToArray();
        var revert = spec.DecisionRule.RevertIf.Select(Evaluate).ToArray();
        var guardrail = Evaluate(spec.Guardrail.BreachIf);
        if (keep.Contains(null) || revert.Contains(null) || guardrail is null)
        {
            var missing = spec.DecisionRule.KeepIf.Concat(spec.DecisionRule.RevertIf)
                .Append(spec.Guardrail.BreachIf).Where(c => Evaluate(c) is null).Select(c => c.Metric).Distinct();
            Console.WriteLine($"reading: inconclusive (unavailable comparison or zero baseline: {string.Join(", ", missing)})");
            return;
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
        Console.WriteLine($"reading: {verdict} ({reason})");
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
