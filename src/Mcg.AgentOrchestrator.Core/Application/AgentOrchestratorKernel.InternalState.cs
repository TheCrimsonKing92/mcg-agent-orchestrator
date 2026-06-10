namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private void RefreshGoalStatus(Goal goal)
    {
        if (_humanInputRequests.Values.Any(candidate => candidate.GoalId == goal.Id && !candidate.IsCompleted))
        {
            goal.SetStatus(GoalStatus.WaitingForHuman);
            return;
        }

        if (goal.Tasks.All(task => BuildTaskVerificationGate(task).GateStatus == VerificationGateStatus.Passed))
        {
            goal.SetStatus(GoalStatus.Completed);
            return;
        }

        goal.SetStatus(GoalStatus.Active);
    }

    private static TaskVerificationGate BuildTaskVerificationGate(TaskSpec task)
    {
        if (task.LastVerification is { Succeeded: false })
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.FailedVerification,
                $"Latest verification failed with exit {task.LastVerification.ExitCode}: {task.LastVerification.Command}");
        }

        if (HasOutputTokenLimitHit(task.LastExecution))
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.FailedVerification,
                $"Model output may be truncated at {task.LastExecution!.MaxOutputTokens} token(s); retry with narrower scope or stronger model before accepting this gate.");
        }

        if (task.Status != WorkTaskStatus.Completed)
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.NotReady,
                $"Task status is {task.Status}; complete the task before accepting this gate.");
        }

        if (task.LastVerification is null)
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.MissingVerification,
                "Task is completed but has no verification evidence.");
        }

        return new TaskVerificationGate(
            task.Id,
            task.RequiredRole,
            task.Description,
            task.Status,
            VerificationGateStatus.Passed,
            $"Verified by {task.LastVerification.Command} at {task.LastVerification.CompletedAt:u}.");
    }

    private static bool HasOutputTokenLimitHit(TaskExecutionRecord? execution)
    {
        return execution?.MaxOutputTokens is > 0 &&
            execution.Usage?.OutputTokens is { } outputTokens &&
            outputTokens >= execution.MaxOutputTokens.Value;
    }

    private static string BuildVerificationSuggestedAction(TaskVerificationGate gate)
    {
        if (IsOutputTokenLimitGate(gate))
        {
            return "Retry with narrower scope or a stronger model, rerun verification, and record model fit if this was subscription/API work.";
        }

        return BuildVerificationSuggestedAction(gate.GateStatus);
    }

    private static bool IsOutputTokenLimitGate(TaskVerificationGate gate)
    {
        return gate.GateStatus == VerificationGateStatus.FailedVerification &&
            gate.Message.StartsWith("Model output may be truncated", StringComparison.Ordinal);
    }

    private static string BuildVerificationSuggestedAction(VerificationGateStatus status)
    {
        return status switch
        {
            VerificationGateStatus.NotReady => "Complete the task before recording final verification.",
            VerificationGateStatus.MissingVerification => "Record verification with a command or manual pass/fail evidence.",
            VerificationGateStatus.FailedVerification => "Inspect verification history, retry the task after fixes, then rerun verification.",
            VerificationGateStatus.Passed => "No verification work remains.",
            _ => "Inspect the task gate before continuing."
        };
    }

    private static GoalAcceptanceBlockerKind BuildAcceptanceBlockerKind(VerificationGateStatus status)
    {
        return status switch
        {
            VerificationGateStatus.NotReady => GoalAcceptanceBlockerKind.VerificationNotReady,
            VerificationGateStatus.MissingVerification => GoalAcceptanceBlockerKind.VerificationMissing,
            VerificationGateStatus.FailedVerification => GoalAcceptanceBlockerKind.VerificationFailed,
            _ => GoalAcceptanceBlockerKind.VerificationNotReady
        };
    }

    private static HumanInputWorkItem BuildHumanInputWorkItem(Goal goal, HumanInputRequest request)
    {
        TaskSpec? task = null;
        if (request.TaskId is not null)
        {
            task = goal.FindTask(request.TaskId);
        }

        return new HumanInputWorkItem(
            request.Id,
            request.TaskId,
            task?.RequiredRole,
            task?.Description,
            task?.Status,
            request.Question,
            request.RequestedAt,
            "Answer the pending human input request.");
    }

    private static ProcessBatchPlanItem BuildProcessBatchPlanItem(TaskSpec task, ProcessBatchActionKind action)
    {
        var (status, reason) = action switch
        {
            ProcessBatchActionKind.StartDispatches => GetStartDispatchReadiness(task),
            ProcessBatchActionKind.RefreshDispatches => GetRefreshDispatchReadiness(task),
            ProcessBatchActionKind.CancelDispatches => GetCancelDispatchReadiness(task),
            _ => (ProcessBatchItemStatus.Skipped, "Unsupported batch action.")
        };

        return new ProcessBatchPlanItem(
            task.Id,
            task.RequiredRole,
            task.Description,
            task.Status,
            status,
            reason);
    }

    private static (ProcessBatchItemStatus Status, string Reason) GetStartDispatchReadiness(TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Running)
        {
            return (ProcessBatchItemStatus.Skipped, $"Task status is {task.Status}; only running dispatched tasks can be started.");
        }

        if (task.LastDispatch is null)
        {
            return (ProcessBatchItemStatus.Skipped, "Task has no recorded dispatch.");
        }

        if (task.LastProcess is { IsRunning: true })
        {
            return (ProcessBatchItemStatus.Skipped, $"Task already has a running process pid={task.LastProcess.ProcessId}.");
        }

        return (ProcessBatchItemStatus.Ready, $"Ready to start dispatch: {task.LastDispatch.WorkerName}.");
    }

    private static (ProcessBatchItemStatus Status, string Reason) GetRefreshDispatchReadiness(TaskSpec task)
    {
        if (task.LastProcess is null)
        {
            return (ProcessBatchItemStatus.Skipped, "Task has no background process to refresh.");
        }

        if (!task.LastProcess.IsRunning)
        {
            return (ProcessBatchItemStatus.Skipped, $"Task process already completed with exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}.");
        }

        return (ProcessBatchItemStatus.Ready, $"Ready to refresh process pid={task.LastProcess.ProcessId}.");
    }

    private static (ProcessBatchItemStatus Status, string Reason) GetCancelDispatchReadiness(TaskSpec task)
    {
        if (task.LastProcess is null)
        {
            return (ProcessBatchItemStatus.Skipped, "Task has no background process to cancel.");
        }

        if (!task.LastProcess.IsRunning)
        {
            return (ProcessBatchItemStatus.Skipped, $"Task process already completed with exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}.");
        }

        return (ProcessBatchItemStatus.Ready, $"Ready to cancel process pid={task.LastProcess.ProcessId}.");
    }
}
