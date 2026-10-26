using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.ReadModels;

internal static class ExperimentHoursToLand
{
    internal static double? Mean(IReadOnlyCollection<Goal> sideGoals,
        IReadOnlyDictionary<string, DateTimeOffset> landings, List<string> diagnostics, string side)
    {
        if (sideGoals.Count == 0) return null;
        var totalHours = 0.0;
        foreach (var goal in sideGoals)
        {
            if (goal.Status != GoalStatus.Completed || !landings.TryGetValue(goal.Id.Value, out var landed))
            {
                diagnostics.Add($"{side} goal '{goal.Id.Value}' has no landing time");
                return null;
            }
            totalHours += GoalTimingReport.Build(goal,
                new GoalTimingReportContext(LandedAt: landed, LandingSource: "experiment-landing-event"))
                .TotalDuration.TotalHours;
        }
        return totalHours / sideGoals.Count;
    }
}
