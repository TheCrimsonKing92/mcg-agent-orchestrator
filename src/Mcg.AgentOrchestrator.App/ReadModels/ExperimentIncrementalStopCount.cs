using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal static class ExperimentIncrementalStopCount
{
    internal static int Count(ExperimentRecord effective, IReadOnlyCollection<Goal> goals)
    {
        var baseline = effective.Spec.Baseline;
        return (baseline.BaselineGoalIds ?? []).Concat(baseline.ComparisonGoalIds ?? [])
            .Select(Resolve).OfType<Goal>().Where(goal => goal.Status == GoalStatus.Completed)
            .DistinctBy(goal => goal.Id).Count();

        Goal? Resolve(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            reference = reference.Trim();
            var exact = goals.FirstOrDefault(goal => string.Equals(goal.Id.Value, reference, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var matches = goals.Where(goal => goal.Id.Value.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
