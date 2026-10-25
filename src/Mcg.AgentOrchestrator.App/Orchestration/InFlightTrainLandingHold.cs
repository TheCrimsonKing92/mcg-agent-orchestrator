using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Solo landings must not move the base of another goal's active merge train.
internal static class InFlightTrainLandingHold
{
    internal static string? Describe(GoalId goal, ConductorAcceptanceCapacitySnapshot active)
    {
        ArgumentNullException.ThrowIfNull(active);
        var train = active.ActiveRoots
            .Where(root => root.Key.StartsWith("train:", StringComparison.Ordinal) &&
                !root.MemberGoalIds.Contains(goal.Value))
            .OrderBy(root => root.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return train is null ? null :
            $"merge train {train.Key} is in flight and goal {goal.Value[..Math.Min(8, goal.Value.Length)]} is not a member; landing waits for the train to settle";
    }
}
