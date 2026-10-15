using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExitedDispatchReconciliationExecutor
{
    internal static bool ReconcileTask(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, BackgroundDispatchRunner dispatchRunner)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (task.CompletionBuildEvidence is { } operation &&
            (!BackgroundDispatchRunner.HasCurrentCompletionBuildWorker(task) || !operation.IsPending && !operation.CustodyReleased))
        {
            var disposition = dispatchRunner.ReconcileCompletionBuildEvidence(kernel, goalId, taskId,
                CompletionBuildEvidenceTarget.From(operation));
            if (disposition.IsPending || !disposition.CustodyReleased)
                return false;
            task = kernel.GetTask(goalId, taskId);
            if (task.LastProcess is null)
                return true;
        }
        dispatchRunner.RefreshLatestProcessWithOutcome(kernel, goalId, taskId);
        task = kernel.GetTask(goalId, taskId);
        return task.LastProcess is { } process &&
               DispatchProcessCompletionState.HasAlreadyBeenApplied(task, process);
    }

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
