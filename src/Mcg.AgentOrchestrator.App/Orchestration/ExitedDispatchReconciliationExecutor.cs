using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExitedDispatchReconciliationExecutor
{
    internal static Goal Execute(
        Goal goal,
        Func<Goal, TaskId, bool> reconcile,
        Func<Goal, Goal> readCurrentGoal)
    {
        var exitedUnappliedTaskIds = goal.Tasks
            .Where(task => task.CompletionBuildEvidence is { } operation &&
                           (operation.IsPending || !operation.CustodyReleased) ||
                           task.LastProcess is { } process &&
                           DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process))
            .Select(task => task.Id)
            .ToArray();
        foreach (var taskId in exitedUnappliedTaskIds)
        {
            reconcile(goal, taskId);
            goal = readCurrentGoal(goal);
        }

        return goal;
    }
}
