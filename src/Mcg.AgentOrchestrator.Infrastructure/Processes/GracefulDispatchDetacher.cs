using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GracefulDispatchDetacher
{
    internal static int DetachRunningProcessesForGoal(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        Action<TaskProcessRecord> evictProcessLogCache,
        Action<TaskId> cancelAfterDetachFailure,
        Func<int, bool> isStillRunning,
        DateTimeOffset interruptedAt)
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

            if (WorkerProcessJobs.TryDetachForGracefulStop(
                    process,
                    $"{goalId.Value}:{task.Id.Value}",
                    out var detachedProcess,
                    out var safeToCancelOnFailure,
                    out var requeueWithoutTerminationOnFailure,
                    out var detachFailure))
            {
                kernel.RecordTaskProcessGracefullyDetached(
                    goalId,
                    task.Id,
                    detachedProcess with { WasGracefullyDetachedByConductor = true });
                evictProcessLogCache(process);
                detached++;
                continue;
            }

            if (requeueWithoutTerminationOnFailure && !isStillRunning(process.ProcessId))
            {
                kernel.RecordTaskProcessGracefullyDetached(
                    goalId,
                    task.Id,
                    process with { WasGracefullyDetachedByConductor = true });
                evictProcessLogCache(process);
                detached++;
            }
            else if (safeToCancelOnFailure)
            {
                cancelAfterDetachFailure(task.Id);
                kernel.RecordTaskNote(
                    goalId,
                    task.Id,
                    $"{detachFailure}; task marked conductor-cancelled so a successor can requeue it.");
            }
            else if (requeueWithoutTerminationOnFailure)
            {
                kernel.RecordTaskProcessCancelled(
                    goalId,
                    task.Id,
                    process with
                    {
                        CompletedAt = interruptedAt,
                        WasCancelled = true,
                        WasCancelledByConductor = true,
                        ExitArtifactOrigin = DispatchExitArtifactOrigin.Synthetic,
                        ExitArtifactReason = detachFailure
                    },
                    CancellationCandidateEvidence.Indeterminate(
                        "dispatch identity could not be proven; no operating-system termination was attempted"));
                evictProcessLogCache(process);
                kernel.RecordTaskNote(
                    goalId,
                    task.Id,
                    $"{detachFailure}; task marked conductor-interrupted for requeue without terminating an unproven process identity.");
            }
            else
            {
                kernel.ReportTaskProgress(
                    goalId,
                    task.Id,
                    WorkTaskStatus.Failed,
                    $"{detachFailure}; refusing cancellation because durable ownership could not be proved.");
                continue;
            }
        }

        return detached;
    }
}
