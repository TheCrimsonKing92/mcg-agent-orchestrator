using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ExperimentTwinValues(
    IReadOnlyDictionary<string, (double? Before, double? After)> Values,
    IReadOnlyList<string> Diagnostics, string Goals);

internal static class ExperimentTwinReading
{
    internal static ExperimentTwinValues Compute(ExperimentBaseline baseline, IReadOnlyCollection<Goal> goals,
        IReadOnlyCollection<AppliedRetryIntent> intents)
    {
        var diagnostics = new List<string>();
        var twin = Resolve(baseline.TwinGoalId, "baseline");
        var comparison = Resolve(baseline.ComparisonGoalId, "comparison");
        var before = Totals(twin, "baseline");
        var after = Totals(comparison, "comparison");
        if (twin is not null && comparison is not null && twin.Id == comparison.Id)
        {
            diagnostics.Add("twin and comparison resolve to the same goal");
            after = null;
        }
        var values = ExperimentMetrics.Menu.ToDictionary(metric => metric, metric => (
            Before: metric == "landings-per-hour" ? null : ExperimentReading.TotalsValue(metric, before),
            After: metric == "landings-per-hour" ? null : ExperimentReading.TotalsValue(metric, after)));
        return new(values, diagnostics,
            $"baseline={twin?.Id.Value ?? baseline.TwinGoalId ?? "none"} comparison={comparison?.Id.Value ?? baseline.ComparisonGoalId ?? "none"}");

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

        RoundValueTotals? Totals(Goal? goal, string side)
        {
            if (goal is null) return null;
            if (goal.Status != GoalStatus.Completed)
            {
                diagnostics.Add($"{side} goal '{goal.Id.Value}' has not landed");
                return null;
            }
            var totals = RoundValueReport.Build([goal], DateTimeOffset.MinValue, DateTimeOffset.MaxValue, intents).Window;
            if (totals.LandedGoals > 0) return totals;
            diagnostics.Add($"{side} goal '{goal.Id.Value}' has no landed round totals");
            return null;
        }
    }
}
