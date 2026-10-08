using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns dependency hold classification; the batch loop consumes the resulting scheduling decision.
internal static class ConductorDependencyHoldEvaluator
{
    internal static string? Evaluate(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel,
        out bool requiresPerson,
        IReadOnlySet<string>? dependencyEscalatedGoals = null)
    {
        requiresPerson = true;
        foreach (var depId in goal.DependsOn)
        {
            var producer = kernel.Goals.FirstOrDefault(candidate => candidate.Id == depId);
            if (producer is not null && SliceBatchSiblingDependencyCoordinator.IsSiblingEdge(goal, producer))
            {
                if (SliceBatchSiblingDependencyCoordinator.IsSatisfiedSibling(goal, producer))
                    continue;
                requiresPerson = producer.Status is GoalStatus.Parked or GoalStatus.WaitingForHuman;
                return SliceBatchSiblingDependencyCoordinator.DescribeHold(producer);
            }

            if (completedGoals.Contains(depId.Value) ||
                kernel.IsKnownCompletedDependencyGoal(depId) ||
                (kernel.TryGetKnownDependencyGoalStatus(depId, out var dependencyStatus) &&
                 IsMetadataSatisfiedDependencyStatus(dependencyStatus)))
            {
                continue;
            }

            if (kernel.TryGetKnownDependencyGoalStatus(depId, out var terminalStatus) &&
                IsTerminalWithoutLandingDependencyStatus(terminalStatus))
            {
                return $"dependency-terminal-without-landing: {depId.Value[..8]} state={terminalStatus}";
            }

            if (escalatedGoals.Contains(depId.Value) && dependencyEscalatedGoals?.Contains(depId.Value) != true)
                return $"dependency escalated: {depId.Value[..8]}";

            if (kernel.TryGetKnownDependencyGoalStatus(depId, out var knownStatus))
            {
                requiresPerson = knownStatus.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    knownStatus.Equals(GoalStatus.WaitingForHuman.ToString(), StringComparison.OrdinalIgnoreCase);
                if (knownStatus.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
                    return $"waiting on dependency {depId.Value[..8]}";
            }

            var depPrefix = kernel.Goals.FirstOrDefault(g => g.Id == depId)?.Id.Value[..8] ?? depId.Value[..8];
            return $"waiting on dependency {depPrefix}";
        }

        return null;
    }

    private static bool IsMetadataSatisfiedDependencyStatus(string status) =>
        status.Equals("CleanedUp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalWithoutLandingDependencyStatus(string status) =>
        status.Equals(GoalStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase);
}
