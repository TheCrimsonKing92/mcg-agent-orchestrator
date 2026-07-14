namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int ProviderConnectivityRetryLimit = 3;

    public void RecordTaskVerification(GoalId goalId, TaskId taskId, TaskVerificationRecord verification)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RecordVerification(verification);

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Verification {status} ({verification.ExitCode}): {verification.Command}");
        if (TryFailWorkerResultBlocker(goalId, task, verification, enforceFailureEvidenceRule: false))
        {
            return;
        }

        TryCompleteTaskWithPassingVerification(
            goal,
            task,
            BuildCompletionMessageWithAdvisoryBlocker(
                $"Task completed with passing verification: {verification.Command}",
                task,
                verification));
        RefreshGoalStatus(goal);
    }

    public void RecordDispatchExecutionResult(
        GoalId goalId,
        TaskId taskId,
        TaskVerificationRecord verification,
        ProviderFailureKind providerFailureKind = ProviderFailureKind.Unknown)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no dispatch to execute.");
        }

        if (!string.Equals(task.LastDispatch.Command, verification.Command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Dispatch execution evidence command does not match the recorded dispatch command.");
        }

        if (!string.Equals(task.LastDispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Dispatch execution evidence working directory does not match the recorded dispatch working directory.");
        }

        if (task.LatestRetryAt is { } latestRetryAt &&
            task.LastDispatch.DispatchedAt <= latestRetryAt)
        {
            var staleDispatchAt = task.LastDispatch.DispatchedAt;
            task.ClearLastDispatch();
            task.ClearLastProcess();
            task.ClearLatestVerification();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                $"Ignored stale dispatch execution evidence from {staleDispatchAt:u}; latest retry was {latestRetryAt:u}.");
            RefreshGoalStatus(goal);
            return;
        }

        if (providerFailureKind != ProviderFailureKind.Unknown &&
            verification.ProviderFailureKind == ProviderFailureKind.Unknown)
        {
            verification = verification with { ProviderFailureKind = providerFailureKind };
        }

        task.RecordVerification(verification);

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Dispatch execution {status} ({verification.ExitCode}): {verification.Command}");
        if (TryFailWorkerResultBlocker(goalId, task, verification, enforceFailureEvidenceRule: true))
        {
            return;
        }

        var effectiveProviderFailureKind = verification.ProviderFailureKind;
        var outcome = ShouldUseTypedProviderFailureOutcome(effectiveProviderFailureKind)
            ? DispatchFailureClassifier.ClassifyProviderFailure(
                effectiveProviderFailureKind,
                verification.ExitCode,
                string.IsNullOrWhiteSpace(verification.StandardOutput) &&
                string.IsNullOrWhiteSpace(verification.StandardError),
                BuildTypedProviderFailureEvidenceSummary(verification))
            : DispatchFailureClassifier.Classify(task, verification, effectiveProviderFailureKind);
        if (!string.IsNullOrWhiteSpace(outcome.ClassifierReceipt))
        {
            Append(goal, taskId, ProgressKind.TaskNote, outcome.ClassifierReceipt);
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.RecoverableSubscriptionLimit)
        {
            if (DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out var retryAfter))
            {
                task.SetSubscriptionRetryAfter(retryAfter);
            }

            var recoverableLimitFailures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
            task.ClearLatestVerification();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
            var message = recoverableLimitFailures >= DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold
                ? $"Dispatch hit a recoverable subscription usage limit {recoverableLimitFailures} time(s); review model, profile, or timing before redispatch: {task.LastDispatch.Command}"
                : $"Dispatch hit a recoverable subscription usage limit; task is ready to retry later: {task.LastDispatch.Command}";
            Append(goal, taskId, ProgressKind.TaskRetried, message);
            RefreshGoalStatus(goal);
            return;
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.ProviderConnectivity)
        {
            var connectivityFailures = DispatchFailureClassifier.CountRecoverableProviderConnectivityFailures(task);
            if (connectivityFailures <= ProviderConnectivityRetryLimit)
            {
                task.SetSubscriptionRetryAfter(_clock.UtcNow + BuildProviderConnectivityBackoff(connectivityFailures));
                task.RecordRetry(_clock.UtcNow);
                task.ClearLatestVerification();
                task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
                Append(
                    goal,
                    taskId,
                    ProgressKind.TaskRetried,
                    $"Dispatch hit provider connectivity failure; task is ready to retry after bounded backoff (attempt {connectivityFailures}/{ProviderConnectivityRetryLimit}): {task.LastDispatch.Command}");
                RefreshGoalStatus(goal);
                return;
            }

            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch provider connectivity failed after {ProviderConnectivityRetryLimit} automatic retry attempt(s); evidence: {outcome.EvidenceSummary}: {task.LastDispatch.Command}");
            return;
        }

        var humanInputQuestion = AgentOutputDirectives.TryParseHumanInputRequest(verification.StandardOutput)
            ?? AgentOutputDirectives.TryParseHumanInputRequest(verification.StandardError);
        if (humanInputQuestion is not null)
        {
            RequestHumanInput(goal.Id, task.Id, humanInputQuestion);
            return;
        }

        if (outcome.Kind == DispatchOutcomeKind.EmptyOutputFlake &&
            verification.ExitCode == 0 &&
            string.IsNullOrWhiteSpace(verification.StandardOutput) &&
            string.IsNullOrWhiteSpace(verification.StandardError))
        {
            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch exited 0 with no output; verification cannot be confirmed: {task.LastDispatch.Command}");
            return;
        }

        if (!verification.Succeeded &&
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess)
        {
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                "Reconciled failed dispatch verification to Completed from structured WORKER_RESULT evidence and commit provenance.");
        }

        ReportTaskProgress(
            goalId,
            taskId,
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess ? WorkTaskStatus.Completed : WorkTaskStatus.Failed,
            outcome.Kind == DispatchOutcomeKind.VerifiedSuccess
                 ? BuildCompletionMessageWithAdvisoryBlocker(
                     $"Dispatch completed successfully: {task.LastDispatch.Command}",
                     task,
                     verification)
                : $"Dispatch failed with exit code {verification.ExitCode}: {task.LastDispatch.Command}");
    }

    private static TimeSpan BuildProviderConnectivityBackoff(int attempt)
    {
        return TimeSpan.FromMinutes(Math.Clamp(attempt, 1, ProviderConnectivityRetryLimit));
    }

    private static bool ShouldUseTypedProviderFailureOutcome(ProviderFailureKind failureKind)
    {
        return failureKind is
            ProviderFailureKind.RateLimit or
            ProviderFailureKind.Connectivity or
            ProviderFailureKind.Sandbox1312;
    }

    private static string BuildTypedProviderFailureEvidenceSummary(TaskVerificationRecord verification)
    {
        foreach (var output in new[] { verification.StandardError, verification.StandardOutput })
        {
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
        }

        return "typed provider failure";
    }

    private bool TryFailWorkerResultBlocker(
        GoalId goalId,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool enforceFailureEvidenceRule)
    {
        if (verification.Succeeded &&
            task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out var blocker))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT reported blocker: {blocker}");
            return true;
        }

        if (enforceFailureEvidenceRule &&
            task.RequiredRole != AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out blocker))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"WORKER_RESULT reported blocker: {blocker}");
            return true;
        }

        return false;
    }

    private static string BuildCompletionMessageWithAdvisoryBlocker(
        string message,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        return (WorkerResultBlockers.TryFindAdvisoryBlocker(verification, out var blocker) ||
                (WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
                 WorkerResultBlockers.TryFindBlocker(verification, out blocker)))
            ? $"{message}; advisory WORKER_RESULT blocker: {blocker}"
            : message;
    }

    public void RecordDispatchBaseCommit(GoalId goalId, TaskId taskId, string baseCommit)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchBaseCommit(baseCommit);
    }

    public void RecordDispatchResultCommit(GoalId goalId, TaskId taskId, string resultCommit)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchResultCommit(resultCommit);
    }

    public void RecordDispatchSandboxLowIntegrity(GoalId goalId, TaskId taskId, bool sandboxLowIntegrity)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchSandboxLowIntegrity(sandboxLowIntegrity);
    }

    public void RecordTaskDispatch(
        GoalId goalId,
        TaskId taskId,
        TaskDispatchRecord dispatch,
        bool allowPendingRecordedDispatchRefresh = false)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has passing verification; retry the task before dispatching it again.");
        }

        if (task.Status != WorkTaskStatus.Assigned &&
            !(allowPendingRecordedDispatchRefresh &&
                task.Status == WorkTaskStatus.Running &&
                task.LastDispatch is not null &&
                task.LastProcess is null))
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; retry or assign it before dispatching it again.");
        }

        task.RecordDispatch(dispatch);
        task.SetStatus(WorkTaskStatus.Running);
        goal.SetStatus(GoalStatus.Active);
        Append(goal, taskId, ProgressKind.TaskDispatchRecorded, $"Dispatched to {dispatch.WorkerName}{FormatDispatchTimelineModelSelection(dispatch)}: {dispatch.Command}");
    }

    private static string FormatDispatchTimelineModelSelection(TaskDispatchRecord dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.ProviderName) || string.IsNullOrWhiteSpace(dispatch.ModelName))
        {
            return string.Empty;
        }

        return $" using {dispatch.ProviderName}/{dispatch.ModelName}";
    }

    public void RecordTaskProcessStarted(GoalId goalId, TaskId taskId, TaskProcessRecord process)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no dispatch to start.");
        }

        if (!string.Equals(task.LastDispatch.Command, process.Command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Process command does not match the recorded dispatch command.");
        }

        if (!string.Equals(task.LastDispatch.WorkingDirectory, process.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Process working directory does not match the recorded dispatch working directory.");
        }

        task.RecordProcess(process);
        task.SetStatus(WorkTaskStatus.Running);
        goal.SetStatus(GoalStatus.Active);
        Append(goal, taskId, ProgressKind.TaskProcessStarted, $"Started process {process.ProcessId}: {process.Command}");
    }

    public void RecordTaskProcessRefreshed(
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord process,
        TaskVerificationRecord? verification,
        ProviderFailureKind providerFailureKind = ProviderFailureKind.Unknown)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RecordProcess(process);

        if (verification is not null)
        {
            RecordDispatchExecutionResult(goalId, taskId, verification, providerFailureKind);
        }
    }

    public void RecordTaskProcessCancelled(GoalId goalId, TaskId taskId, TaskProcessRecord process)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.LastProcess is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' has no background process to cancel.");
        }

        if (process.WasCancelled is false)
        {
            throw new InvalidOperationException("Cancelled process record must have WasCancelled set.");
        }

        task.RecordProcess(process);
        task.SetStatus(WorkTaskStatus.Cancelled);
        Append(goal, taskId, ProgressKind.TaskCancelled, $"Cancelled process {process.ProcessId}: {process.Command}");
    }

    private bool TryCompleteTaskWithPassingVerification(Goal goal, TaskSpec task, string message)
    {
        if (task.Status == WorkTaskStatus.Completed || task.LastVerification is not { Succeeded: true })
        {
            return false;
        }

        if (_humanInputRequests.Values.Any(request => request.GoalId == goal.Id && request.TaskId == task.Id && !request.IsCompleted))
        {
            return false;
        }

        task.SetStatus(WorkTaskStatus.Completed);
        Append(goal, task.Id, ProgressKind.TaskCompleted, message);
        return true;
    }
}
