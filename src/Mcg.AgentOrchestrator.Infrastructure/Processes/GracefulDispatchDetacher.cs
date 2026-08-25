using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GracefulDispatchDetacher
{
    internal static int DetachRunningProcessesForGoal(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        Action<TaskProcessRecord> evictProcessLogCache,
        Action<TaskId> cancelAfterDetachFailure)
    {
        var goal = kernel.GetGoal(goalId);
        var detached = 0;
        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is not { IsRunning: true } process ||
                process.WasGracefullyDetachedByConductor)
            {
                continue;
            }

            if (WorkerProcessJobs.TryDetachForGracefulStop(process.ProcessId, out var detachFailure))
            {
                kernel.RecordTaskProcessGracefullyDetached(
                    goalId,
                    task.Id,
                    process with { WasGracefullyDetachedByConductor = true });
                evictProcessLogCache(process);
                detached++;
                continue;
            }

            cancelAfterDetachFailure(task.Id);
            kernel.RecordTaskNote(
                goalId,
                task.Id,
                $"{detachFailure}; task marked conductor-cancelled so a successor can requeue it.");
        }

        return detached;
    }
}
