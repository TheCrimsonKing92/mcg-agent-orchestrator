using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ExperimentCohortReading
{
    internal static ExperimentTwinValues Compute(ExperimentBaseline baseline, IReadOnlyCollection<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var diagnostics = new List<string>();
        var before = Totals(baseline.BaselineGoalIds, "baseline", out var baselineGoals, out var baselineLabels);
        var after = Totals(baseline.ComparisonGoalIds, "comparison", out var comparisonGoals, out var comparisonLabels);
        foreach (var goal in comparisonGoals)
        {
            if (!baselineGoals.Any(other => other.Id == goal.Id)) continue;
            diagnostics.Add($"baseline and comparison cohorts share goal '{goal.Id.Value}'");
            after = null;
        }
        var values = ExperimentMetrics.Menu.ToDictionary(metric => metric, metric => (
            Before: metric == "landings-per-hour" ? null : ExperimentReading.TotalsValue(metric, before),
            After: metric == "landings-per-hour" ? null : ExperimentReading.TotalsValue(metric, after)));
        return new(values, diagnostics,
            $"baseline={string.Join(",", baselineLabels)} comparison={string.Join(",", comparisonLabels)}");

        RoundValueTotals? Totals(IReadOnlyList<string>? references, string side,
            out List<Goal> resolved, out List<string> labels)
        {
            resolved = [];
            labels = [];
            var initialDiagnostics = diagnostics.Count;
            if (references is null || references.Count == 0)
            {
                diagnostics.Add($"{side} goal ids not declared");
                labels.Add("none");
                return null;
            }
            foreach (var reference in references)
            {
                var goal = Resolve(reference, side);
                if (goal is null)
                {
                    labels.Add(reference ?? "none");
                    continue;
                }
                if (resolved.Any(other => other.Id == goal.Id)) continue;
                resolved.Add(goal);
                labels.Add(goal.Id.Value);
                if (goal.Status != GoalStatus.Completed)
                    diagnostics.Add($"{side} goal '{goal.Id.Value}' has not landed");
                else if (RoundValueReport.Build([goal], DateTimeOffset.MinValue, DateTimeOffset.MaxValue, intents).Window.LandedGoals == 0)
                    diagnostics.Add($"{side} goal '{goal.Id.Value}' has no landed round totals");
            }
            // Per-member reports above validate completeness; metrics always use the pooled report.
            return diagnostics.Count != initialDiagnostics ? null :
                RoundValueReport.Build(resolved, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, intents).Window;
        }

        Goal? Resolve(string? reference, string side)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                diagnostics.Add($"{side} goal id not declared");
                return null;
            }
            reference = reference.Trim();
            var exact = goals.FirstOrDefault(goal => string.Equals(goal.Id.Value, reference, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var matches = goals.Where(goal => goal.Id.Value.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            diagnostics.Add($"{side} goal '{reference}' {(matches.Length == 0 ? "not found" : "is ambiguous")}");
            return null;
        }
    }
}
