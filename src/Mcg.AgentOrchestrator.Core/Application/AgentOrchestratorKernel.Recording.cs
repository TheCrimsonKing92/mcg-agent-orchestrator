namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int ProviderConnectivityRetryLimit = 3;

    public void RecordTaskVerification(GoalId goalId, TaskId taskId, TaskVerificationRecord verification)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        verification = PrepareReviewFindingRecord(
            goal,
            task,
            AttachAuthoritativeReviewFindingContext(task, verification));
        task.RecordVerification(verification);

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Verification {status} ({verification.ExitCode}): {verification.Command}");
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        if (outcome.Kind == DispatchOutcomeKind.VerificationInconclusive)
        {
            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Tester verification inconclusive; same Tester retry or operator escalation required. {outcome.EvidenceSummary}");
            return;
        }

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
            (task.LastDispatch.DispatchedAt < latestRetryAt ||
             (task.LastDispatch.DispatchedAt == latestRetryAt &&
              task.LastVerification is null &&
              task.Status != WorkTaskStatus.Running)))
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

        if (task.LastVerification is { } latestVerification &&
            IsDuplicateDispatchExecutionResult(latestVerification, verification))
        {
            Append(
                goal,
                taskId,
                ProgressKind.TaskNote,
                $"Ignored duplicate dispatch execution evidence for already settled dispatch: {verification.Command}");
            RefreshGoalStatus(goal);
            return;
        }

        verification = PrepareReviewFindingRecord(
            goal,
            task,
            AttachAuthoritativeReviewFindingContext(task, verification));
        task.RecordVerification(verification);

        var status = verification.Succeeded ? "passed" : "failed";
        Append(goal, taskId, ProgressKind.TaskVerificationRecorded, $"Dispatch execution {status} ({verification.ExitCode}): {verification.Command}");

        var humanInputQuestion = verification.HumanInputQuestion
            ?? AgentOutputDirectives.TryParseHumanInputRequest(verification.StandardOutput)
            ?? AgentOutputDirectives.TryParseHumanInputRequest(verification.StandardError);
        if (humanInputQuestion is not null)
        {
            if (WorkerResultBlockers.TryFindBlocker(verification, out var accompanyingBlocker))
            {
                humanInputQuestion += $"{Environment.NewLine}Accompanying WORKER_RESULT blocker evidence: {accompanyingBlocker}";
            }

            RequestHumanInput(goal.Id, task.Id, humanInputQuestion);
            return;
        }

        var effectiveProviderFailureKind = verification.ProviderFailureKind;
        var outcome = DispatchFailureClassifier.Classify(task, verification, effectiveProviderFailureKind);
        if (verification.WorkerResultPresent &&
            task.RequiredRole is AgentRole.Planner or AgentRole.Researcher &&
            WorkerResultBlockers.TryFindPremiseInvalidEvidence(verification, out var premiseEvidence))
        {
            RequestHumanInput(
                goal.Id,
                task.Id,
                $"{task.RequiredRole} reported premise-invalid: {premiseEvidence}. " +
                "Clarify, supersede, or abandon the goal before Developer dispatch.");
            return;
        }

        if (outcome.Kind == DispatchOutcomeKind.VerificationInconclusive)
        {
            if (!string.IsNullOrWhiteSpace(outcome.ClassifierReceipt))
            {
                Append(goal, taskId, ProgressKind.TaskNote, outcome.ClassifierReceipt);
            }

            ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Tester verification inconclusive; same Tester retry or operator escalation required. {outcome.EvidenceSummary}");
            return;
        }

        if (TryFailWorkerResultBlocker(goalId, task, verification, enforceFailureEvidenceRule: true))
        {
            return;
        }

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

    private static bool IsDuplicateDispatchExecutionResult(
        TaskVerificationRecord latestVerification,
        TaskVerificationRecord verification)
    {
        return string.Equals(latestVerification.Command, verification.Command, StringComparison.Ordinal) &&
            string.Equals(latestVerification.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            latestVerification.ExitCode == verification.ExitCode &&
            string.Equals(latestVerification.StandardOutput, verification.StandardOutput, StringComparison.Ordinal) &&
            string.Equals(latestVerification.StandardError, verification.StandardError, StringComparison.Ordinal) &&
            latestVerification.CompletedAt == verification.CompletedAt &&
            latestVerification.ProviderFailureKind == verification.ProviderFailureKind;
    }

    private bool TryFailWorkerResultBlocker(
        GoalId goalId,
        TaskSpec task,
        TaskVerificationRecord verification,
        bool enforceFailureEvidenceRule)
    {
        var goal = GetGoal(goalId);
        IReadOnlyList<ReviewFinding> mergedFindings = verification.MergedReviewFindings ?? [];
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            verification.MergedReviewFindings is null &&
            !TryBuildMergedReviewFindingState(
                goal,
                verification,
                out mergedFindings,
                out var findingDiagnostic))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT structured findings invalid: {findingDiagnostic}");
            return true;
        }

        ReviewFindings.TryGetEffectiveOpenFindings(
            mergedFindings,
            goal.EffectiveAcceptanceCriteriaCorrections,
            out _,
            out var suppressedFindings);
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer)
        {
            foreach (var item in suppressedFindings)
            {
                Append(
                    goal,
                    task.Id,
                    ProgressKind.TaskNote,
                    $"Suppressed Reviewer structured finding matching operator criteria correction: stable_id={item.Finding.StableId}; finding: {item.Finding.Description}; superseded criterion: {item.Correction.SupersededCriterion}; correction recorded {item.Correction.RecordedAt:u} by {item.Correction.Actor}.");
            }
        }

        var openBlockingFindings = ReviewFindings.GetOpenBlockingFindings(
            mergedFindings,
            goal.EffectiveAcceptanceCriteriaCorrections);
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            openBlockingFindings.Count > 0)
        {
            var openIds = string.Join(
                ", ",
                openBlockingFindings.Select(finding => finding.StableId));
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                $"Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking stable_id(s): {openIds}.");
            return true;
        }

        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.TryFindPassVerdict(verification))
        {
            ReportTaskProgress(
                goalId,
                task.Id,
                WorkTaskStatus.Failed,
                "Reviewer WORKER_RESULT verdict rejected: zero open blocking structured findings requires verdict: pass.");
            return true;
        }

        if (verification.Succeeded &&
            task.RequiredRole == AgentRole.Reviewer &&
            !WorkerResultBlockers.IsAdvisoryNoChangeContractBlocker(task, verification) &&
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out var blocker))
        {
            if (TrySuppressSupersededReviewerBlocker(goal, task, blocker, out var effectiveBlocker))
            {
                if (string.IsNullOrWhiteSpace(effectiveBlocker))
                {
                    return false;
                }

                blocker = effectiveBlocker;
            }

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

    public IReadOnlyList<ReviewFinding> GetOpenAdvisoryReviewFindings(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var finalReviewRecord = goal.Tasks
            .Where(task =>
                task.RequiredRole == AgentRole.Reviewer &&
                task.Status == WorkTaskStatus.Completed)
            .Select(task => task.LastVerification)
            .OfType<TaskVerificationRecord>()
            .OrderBy(verification => verification.CompletedAt)
            .LastOrDefault();

        return finalReviewRecord?.GetOpenAdvisoryFindings(goal.EffectiveAcceptanceCriteriaCorrections) ?? [];
    }

    private static TaskVerificationRecord AttachAuthoritativeReviewFindingContext(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (task.RequiredRole != AgentRole.Reviewer || !verification.WorkerResultPresent)
        {
            return verification;
        }

        return verification with
        {
            ReviewFindingTouchedAnchors = task.LastDispatch?.ReviewFindingTouchedAnchors ?? [],
            ReviewedCommit = task.LastDispatch?.BaseCommit
        };
    }

    private static TaskVerificationRecord PrepareReviewFindingRecord(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (!verification.WorkerResultPresent ||
            task.RequiredRole != AgentRole.Reviewer ||
            !TryBuildMergedReviewFindingState(goal, verification, out var mergedFindings, out _))
        {
            return verification;
        }

        return verification with { MergedReviewFindings = mergedFindings };
    }

    private static bool TryBuildMergedReviewFindingState(
        Goal goal,
        TaskVerificationRecord currentVerification,
        out IReadOnlyList<ReviewFinding> state,
        out string diagnostic)
    {
        state = [];
        diagnostic = string.Empty;
        var historicalVerifications = goal.Tasks
            .Where(candidate => candidate.RequiredRole == AgentRole.Reviewer)
            .SelectMany(candidate => candidate.VerificationHistory)
            .Where(candidate => !ReferenceEquals(candidate, currentVerification));
        foreach (var verification in historicalVerifications
            .Append(currentVerification)
            .OrderBy(candidate => candidate.CompletedAt))
        {
            var isCurrentRound = ReferenceEquals(verification, currentVerification);
            if (!WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var parseDiagnostic))
            {
                if (isCurrentRound)
                {
                    diagnostic = parseDiagnostic;
                    return false;
                }

                continue;
            }

            try
            {
                state = ReviewFindingConvergence.ApplyRound(state, round);
            }
            catch (ReviewFindingConvergenceException ex)
            {
                // Reject only the round being recorded. A HISTORICAL round that cannot be folded was already
                // rejected when it was recorded; replaying it must not block every later review from being
                // evaluated, and must not report a stale violation as though it described the new submission.
                if (isCurrentRound)
                {
                    diagnostic = $"{ex.Code}: {ex.Message}";
                    return false;
                }
            }
        }

        return true;
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

    public void RecordDispatchSpawnReceipt(
        GoalId goalId,
        TaskId taskId,
        string command,
        string? providerSessionId,
        string? worktreeHeadSha,
        string? dirtyStateHash)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchSpawnReceipt(command, providerSessionId, worktreeHeadSha, dirtyStateHash);
    }

    public void RecordDispatchProviderSessionId(GoalId goalId, TaskId taskId, string providerSessionId)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetDispatchProviderSessionId(providerSessionId);
    }

    public void RetireDispatchProviderSession(GoalId goalId, TaskId taskId, DateTimeOffset retiredAt)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RetireDispatchProviderSession(retiredAt);
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

        var lane = string.IsNullOrWhiteSpace(dispatch.DispatchLane)
            ? string.Empty
            : $" lane={dispatch.DispatchLane}";
        var reason = string.IsNullOrWhiteSpace(dispatch.ModelSelectionReason)
            ? string.Empty
            : $" ({dispatch.ModelSelectionReason})";
        return $" using {dispatch.ProviderName}/{dispatch.ModelName}{lane}{reason}";
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
