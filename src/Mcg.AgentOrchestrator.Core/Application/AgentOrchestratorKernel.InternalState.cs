namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private void RefreshGoalStatus(Goal goal, bool allowParkedRefresh = false)
    {
        if (IsTerminalGoalStatus(goal.Status))
        {
            return;
        }

        if (goal.Status == GoalStatus.Parked && !allowParkedRefresh)
        {
            return;
        }

        if (goal.Status is GoalStatus.Verifying or GoalStatus.AcceptanceFailed)
        {
            return;
        }

        if (_humanInputRequests.Values.Any(candidate => candidate.GoalId == goal.Id && !candidate.IsCompleted))
        {
            goal.SetStatus(GoalStatus.WaitingForHuman);
            return;
        }

        if (goal.Tasks.All(task => BuildTaskVerificationGate(goal, task).GateStatus == VerificationGateStatus.Passed))
        {
            goal.SetStatus(GoalStatus.Verified);
            return;
        }

        goal.SetStatus(GoalStatus.Active);
    }

    private static bool IsTerminalGoalStatus(GoalStatus status) =>
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static TaskVerificationGate BuildTaskVerificationGate(Goal goal, TaskSpec task)
    {
        if (task.LastVerification is { Succeeded: false })
        {
            var isDirty = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery);
            var message = isDirty
                ? BuildDirtyDispatchRecoveryMessage(recovery)
                : $"Latest verification failed with exit {task.LastVerification.ExitCode}: {task.LastVerification.Command}";
            var reason = isDirty
                ? (recovery.HasUsefulVerification ? VerificationGateReason.DirtyUsefulRecovery : VerificationGateReason.DirtyUnverifiedRecovery)
                : VerificationGateReason.VerificationFailed;
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.FailedVerification,
                message,
                reason);
        }

        if (HasOutputTokenLimitHit(task.LastExecution))
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.FailedVerification,
                $"Model output may be truncated at {task.LastExecution!.MaxOutputTokens} token(s); retry with narrower scope or stronger model before accepting this gate.",
                VerificationGateReason.OutputTokenLimit);
        }

        if (task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, task.LastVerification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(task.LastVerification, out var reviewerBlocker) &&
            TryGetUnsuppressedReviewerBlocker(goal, reviewerBlocker, out var effectiveReviewerBlocker))
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.FailedVerification,
                $"Reviewer WORKER_RESULT reported blocker: {effectiveReviewerBlocker}",
                VerificationGateReason.ReviewerWorkerResultBlocker);
        }

        if (task.Status == WorkTaskStatus.Cancelled)
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.Passed,
                "Task was deliberately cancelled and is excluded from acceptance.",
                VerificationGateReason.Passed);
        }

        if (task.Status != WorkTaskStatus.Completed)
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.NotReady,
                $"Task status is {task.Status}; complete the task before accepting this gate.",
                VerificationGateReason.NotReady);
        }

        if (task.LastVerification is null)
        {
            return new TaskVerificationGate(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Status,
                VerificationGateStatus.MissingVerification,
                "Task is completed but has no verification evidence.",
                VerificationGateReason.MissingVerification);
        }

        return new TaskVerificationGate(
            task.Id,
            task.RequiredRole,
            task.Description,
            task.Status,
            VerificationGateStatus.Passed,
            $"Verified by {task.LastVerification.Command} at {task.LastVerification.CompletedAt:u}.",
            VerificationGateReason.Passed);
    }

    private static bool HasOutputTokenLimitHit(TaskExecutionRecord? execution)
    {
        return OutputTokenLimit.IsHit(execution);
    }

    internal static string BuildVerificationSuggestedAction(TaskVerificationGate gate)
    {
        return gate.Reason switch
        {
            VerificationGateReason.OutputTokenLimit =>
                "Retry with narrower scope or a stronger model, rerun verification, and record model fit if this was subscription/API work.",
            VerificationGateReason.ReviewerWorkerResultBlocker =>
                "Inspect the Reviewer blocker, retry the affected task or escalate to the operator, then rerun Reviewer verification.",
            VerificationGateReason.DirtyUsefulRecovery =>
                "Inspect the dirty diff, rerun verification, commit the worker changes explicitly, then record manual verification.",
            VerificationGateReason.DirtyUnverifiedRecovery =>
                "Inspect the dirty diff, run focused verification before committing, then record manual verification.",
            _ => BuildVerificationSuggestedAction(gate.GateStatus)
        };
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

    private HumanInputWorkItem BuildHumanInputWorkItem(Goal goal, HumanInputRequest request)
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
            request.Kind,
            request.IsAutoDefaultable,
            request.IsDismissible,
            request.IsAnswerRequired,
            request.IsExternallyBlocked,
            Math.Max(0, (long)(_clock.UtcNow - request.CreatedAt).TotalSeconds),
            request.ResumeCommand,
            BuildHumanWaitSuggestedAction(request));
    }

    private static string BuildHumanWaitSuggestedAction(HumanInputRequest request)
    {
        if (request.IsExternallyBlocked)
        {
            return $"{request.Kind} is externally blocked; complete the external prerequisite, then resume with `{request.ResumeCommand}`.";
        }

        return request.IsAnswerRequired
            ? $"Answer required for {request.Kind}. Resume with `{request.ResumeCommand}`."
            : $"{request.Kind} can be dismissed or resumed with `{request.ResumeCommand}`.";
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

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out var retryAfter))
        {
            return (ProcessBatchItemStatus.Skipped, $"Recoverable subscription usage limit is deferred; retry after {retryAfter:u}.");
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
