using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static string? GetDependencyHoldReason(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel) =>
        GetDependencyHoldReason(goal, completedGoals, escalatedGoals, kernel, out _);

    private static string? GetDependencyHoldReason(
        Goal goal,
        HashSet<string> completedGoals,
        HashSet<string> escalatedGoals,
        AgentOrchestratorKernel kernel,
        out bool requiresPerson)
    {
        requiresPerson = true;
        foreach (var depId in goal.DependsOn)
        {
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

            if (escalatedGoals.Contains(depId.Value))
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
}
