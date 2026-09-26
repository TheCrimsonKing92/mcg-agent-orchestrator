using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TerminalGoalSweepLifecycleEvents
{
    internal static IReadOnlyDictionary<GoalId, int> Capture(AgentOrchestratorKernel kernel) =>
        kernel.Goals.ToDictionary(goal => goal.Id, goal => goal.Timeline.Count);

    internal static void Publish(
        AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<GoalId, int> baseline,
        IEnumerable<GoalId> goalIds,
        IGoalLifecycleEventWriter writer,
        Action<string> warn,
        IReadOnlyDictionary<GoalId, int>? end = null)
    {
        foreach (var goalId in goalIds.Distinct())
        {
            try
            {
                var goal = kernel.GetGoal(goalId);
                var start = baseline.TryGetValue(goalId, out var count) ? count : 0;
                var endCount = end is not null && end.TryGetValue(goalId, out var limit) ? limit : goal.Timeline.Count;
                foreach (var progressEvent in goal.Timeline.Skip(start).Take(endCount - start))
                    writer.AppendTimelineEvent(progressEvent);
            }
            catch (Exception ex)
            {
                warn($"SWEEP_WARNING kind=goal-events-append-failed goal={goalId.Value[..8]} error={ex.Message}");
            }
        }
    }
}
