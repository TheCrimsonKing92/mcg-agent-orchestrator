namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private const int DuplicateHumanInputSuppressionThreshold = 3;
    public const int DuplicateHumanInputDistinctTaskThreshold = 2;

    public Goal CreateGoal(string objective, IReadOnlyList<TaskSpec>? tasks = null)
        => CreateGoal(GoalId.New(), objective, tasks, sliceBatchParentId: null);

    public Goal CreateGoal(
        string objective,
        IReadOnlyList<TaskSpec>? tasks,
        GoalId? sliceBatchParentId)
        => CreateGoal(GoalId.New(), objective, tasks, sliceBatchParentId);

    public Goal CreateGoal(GoalId id, string objective, IReadOnlyList<TaskSpec>? tasks = null)
        => CreateGoal(id, objective, tasks, sliceBatchParentId: null);

    public Goal CreateGoal(
        GoalId id,
        string objective,
        IReadOnlyList<TaskSpec>? tasks,
        GoalId? sliceBatchParentId)
    {
        ValidateSliceBatchParent(id, sliceBatchParentId);
        var goal = new Goal(
            id,
            objective,
            tasks ?? CreateDefaultSoftwareDevelopmentTasks(),
            _clock.UtcNow,
            sliceBatchParentId);
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, "Goal created.");
        return goal;
    }

    private void ValidateSliceBatchParent(GoalId childId, GoalId? sliceBatchParentId)
    {
        if (sliceBatchParentId is null)
        {
            return;
        }

        if (sliceBatchParentId == childId)
        {
            throw new InvalidOperationException("A slice-batch goal cannot be its own parent.");
        }

        if (!_goals.TryGetValue(sliceBatchParentId, out var parent))
        {
            throw new InvalidOperationException($"Slice-batch parent '{sliceBatchParentId.Value}' was not found.");
        }

        if (parent.SliceBatchParentId is not null)
        {
            throw new InvalidOperationException(
                $"Slice-batch parent '{sliceBatchParentId.Value}' is already a child; nested slice batches are not supported.");
        }
    }

    public DelegationPlan ActivateGoal(GoalId goalId, IReadOnlyList<AgentDefinition> availableAgents)
    {
        var goal = GetGoal(goalId);
        var assignments = new List<TaskAssignment>();

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Pending))
        {
            var agent = SelectAgentForTask(task, goal.Objective, availableAgents);

            if (agent is null)
            {
                continue;
            }

            task.AssignTo(agent.Id);
            assignments.Add(new TaskAssignment(task.Id, agent.Id, agent.Role));
            Append(goal, task.Id, ProgressKind.TaskDelegated, $"Delegated {task.RequiredRole} task to {agent.Name}.");
        }

        if (assignments.Count > 0)
        {
            goal.SetStatus(GoalStatus.Active);
        }

        return new DelegationPlan(goal.Id, assignments);
    }

    public GoalBriefRevisionResult ReviseGoalBrief(
        GoalId goalId,
        string newBrief,
        string? reason = null,
        IReadOnlyList<GoalBriefAnswerSupersession>? answerSupersessions = null)
    {
        lock (_humanInputRequestLock)
        {
            return ReviseGoalBriefCore(goalId, newBrief, reason, answerSupersessions);
        }
    }

    private GoalBriefRevisionResult ReviseGoalBriefCore(
        GoalId goalId,
        string newBrief,
        string? reason,
        IReadOnlyList<GoalBriefAnswerSupersession>? answerSupersessions)
    {
        var goal = GetGoal(goalId);
        var supersessions = answerSupersessions ?? [];
        var duplicateSupersession = supersessions
            .GroupBy(supersession => supersession.RequestId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSupersession is not null)
        {
            throw new ArgumentException(
                $"Clarification '{duplicateSupersession.Key}' can be superseded only once per brief revision.",
                nameof(answerSupersessions));
        }

        foreach (var supersession in supersessions)
        {
            if (!_humanInputRequests.TryGetValue(supersession.RequestId, out var request) || request.GoalId != goal.Id)
            {
                throw new KeyNotFoundException(
                    $"Human input request '{supersession.RequestId}' was not found on goal '{goal.Id.Value}'.");
            }

            if (!request.IsCompleted || request.WasDismissed || request.AuthoritativeAnswer is null)
            {
                throw new InvalidOperationException(
                    $"Human input request '{supersession.RequestId}' has no answered clarification to supersede.");
            }

            if (string.IsNullOrWhiteSpace(supersession.ReplacementAnswer))
            {
                throw new ArgumentException("Replacement clarification answer cannot be empty.", nameof(answerSupersessions));
            }
        }

        var version = goal.ReviseBrief(newBrief, reason, _clock.UtcNow);
        foreach (var supersession in supersessions)
        {
            SupersedeHumanInput(
                goal.Id,
                supersession.RequestId,
                supersession.ReplacementAnswer,
                HumanInputAnswerOrigin.Operator);
        }

        var notYetStarted = goal.Tasks
            .Where(task => task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned)
            .Select(task => task.Id)
            .ToArray();
        var inFlight = goal.Tasks
            .Where(task => task.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman)
            .Select(task => task.Id)
            .ToArray();
        var completed = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Completed)
            .Select(task => task.Id)
            .ToArray();
        var reasonSuffix = version.Reason is null ? string.Empty : $" reason={version.Reason}";
        Append(
            goal,
            null,
            ProgressKind.GoalBriefRevised,
            $"Goal brief revised: v{version.Version - 1} superseded by v{version.Version}; " +
            $"notYetStarted={notYetStarted.Length}; inFlight={inFlight.Length}; completed={completed.Length}.{reasonSuffix}");
        return new GoalBriefRevisionResult(goal.Id, version, notYetStarted, inFlight, completed);
    }

    public TaskSpec AddTask(
        GoalId goalId,
        AgentRole requiredRole,
        string description,
        IReadOnlyList<AgentDefinition>? availableAgents = null,
        string? verificationPlan = null,
        AgentRole? beforeRole = null)
    {
        var goal = GetGoal(goalId);
        var task = new TaskSpec(TaskId.New(), description, requiredRole, verificationPlan);
        if (beforeRole is { } targetRole)
        {
            goal.AddTaskBeforeRole(task, targetRole);
        }
        else
        {
            goal.AddTask(task);
        }

        Append(
            goal,
            task.Id,
            ProgressKind.TaskAdded,
            beforeRole is null
                ? $"Added {requiredRole} task."
                : $"Added {requiredRole} task before {beforeRole}.");

        if (availableAgents is not null)
        {
            var agent = SelectAgentForTask(task, goal.Objective, availableAgents);

            if (agent is not null)
            {
                task.AssignTo(agent.Id);
                goal.SetStatus(GoalStatus.Active);
                Append(goal, task.Id, ProgressKind.TaskDelegated, $"Delegated {task.RequiredRole} task to {agent.Name}.");
            }
        }

        return task;
    }

    // Selects the most cost-effective agent for a task:
    // Simple tasks prefer local (LocalBridge) agents; Complex tasks prefer capable paid agents.
    private static AgentDefinition? SelectAgentForTask(
        TaskSpec task,
        string goalObjective,
        IReadOnlyList<AgentDefinition> availableAgents)
    {
        var eligible = availableAgents
            .Where(candidate => candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole)
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        var complexity = TaskComplexityEstimator.Estimate(task.Description, goalObjective, task.RequiredRole);

        if (complexity == TaskComplexity.Simple)
        {
            var local = eligible.FirstOrDefault(candidate =>
                candidate.Model.SubscriptionMode == SubscriptionMode.LocalBridge);
            if (local is not null)
            {
                return local;
            }
        }
        else if (complexity == TaskComplexity.Complex)
        {
            var paid = eligible.FirstOrDefault(candidate =>
                candidate.Model.SubscriptionMode != SubscriptionMode.LocalBridge);
            if (paid is not null)
            {
                return paid;
            }
        }

        return eligible[0];
    }

    public TaskSpec SetTaskVerificationPlan(GoalId goalId, TaskId taskId, string verificationPlan)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetVerificationPlan(verificationPlan);
        Append(goal, taskId, ProgressKind.TaskVerificationPlanUpdated, "Verification plan updated.");
        return task;
    }

    public TaskSpec RecordTaskNote(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var noteMessage = message.Trim();
        if (string.IsNullOrWhiteSpace(noteMessage))
        {
            throw new ArgumentException("Task note message cannot be empty.", nameof(message));
        }

        RecordEffectiveAcceptanceCriteriaCorrections(goal, taskId, ProgressKind.TaskNote, noteMessage);
        Append(goal, taskId, ProgressKind.TaskNote, noteMessage);
        return task;
    }

    public TaskSpec RecordOperatorTaskNote(
        GoalId goalId,
        TaskId taskId,
        string message,
        IReadOnlyList<string>? gatedDeliverableIds = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var noteMessage = message.Trim();
        if (string.IsNullOrWhiteSpace(noteMessage))
        {
            throw new ArgumentException("Task note message cannot be empty.", nameof(message));
        }

        var recordedAt = _clock.UtcNow;
        var sourceRecordId = $"task-note:{taskId.Value}:{recordedAt.UtcTicks}";
        var gates = (gatedDeliverableIds ?? [])
            .Select(deliverableId => deliverableId.Trim())
            .Where(deliverableId => deliverableId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(deliverableId => new OperatorGateRecord(deliverableId, sourceRecordId, recordedAt))
            .ToArray();
        RecordEffectiveAcceptanceCriteriaCorrections(goal, taskId, ProgressKind.OperatorTaskNote, noteMessage);
        Append(goal, taskId, ProgressKind.OperatorTaskNote, noteMessage, gates, recordedAt);
        return task;
    }

    public TaskSpec RecordReviewerEvidenceRequestReceived(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Reviewer evidence request message cannot be empty.", nameof(message));
        }

        Append(goal, taskId, ProgressKind.ReviewerEvidenceRequestReceived, trimmed);
        return task;
    }

    public TaskSpec RecordReviewerEvidenceRunRecorded(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Reviewer evidence run message cannot be empty.", nameof(message));
        }

        Append(goal, taskId, ProgressKind.ReviewerEvidenceRunRecorded, trimmed);
        return task;
    }

    public TaskSpec RecordFindingEvidenceRequest(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Finding evidence request message cannot be empty.", nameof(message));
        }

        Append(goal, taskId, ProgressKind.FindingEvidenceRequestRecorded, trimmed);
        return task;
    }

    public TaskSpec RecordFindingEvidenceRun(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Finding evidence run message cannot be empty.", nameof(message));
        }

        Append(goal, taskId, ProgressKind.FindingEvidenceRunRecorded, trimmed);
        return task;
    }

    public TaskSpec RecordFindingEvidenceOutcome(
        GoalId goalId,
        TaskId taskId,
        string stableId,
        FindingEvidenceOutcome outcome,
        FindingEvidenceReceipt? receipt = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RecordFindingEvidenceOutcome(stableId, outcome, receipt);
        return task;
    }

    public TaskSpec RecordPreReviewEvidence(
        GoalId goalId,
        TaskId reviewerTaskId,
        PreReviewEvidenceReceipt receipt)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(reviewerTaskId);
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            throw new InvalidOperationException("Pre-review evidence can only be attached to a Reviewer task.");
        }

        if (!string.Equals(receipt.GoalId, goalId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pre-review evidence goal identity does not match the target goal.");
        }

        if (task.RecordPreReviewEvidence(receipt))
        {
            Append(
                goal,
                reviewerTaskId,
                ProgressKind.PreReviewEvidenceRecorded,
                $"Pre-review evidence {receipt.Disposition} for round {receipt.ReviewerRound}, candidate {receipt.CandidateSha}; " +
                $"selected={receipt.SelectedFocusedTests.Count}, passed={receipt.PassedCheckCount}, failed={receipt.FailedCheckCount}, " +
                $"pointer={receipt.EvidencePointer ?? "none"}.");
        }

        return task;
    }

    public TaskSpec RecordPreReviewMappingEscalationSuppressed(
        GoalId goalId,
        TaskId reviewerTaskId,
        string candidateSha,
        int suppressedCount)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(reviewerTaskId);
        Append(
            goal,
            reviewerTaskId,
            ProgressKind.PreReviewMappingEscalationSuppressed,
            $"Pre-review mapping escalation suppressed: goal={goalId.Value}; candidate_sha={candidateSha}; " +
            $"disposition={PreReviewEvidenceDisposition.MappingNeedsInput}; suppressed_count={suppressedCount}.");
        return task;
    }

    public TaskSpec RetryTask(
        GoalId goalId,
        TaskId taskId,
        string message,
        bool invalidateDownstream = true,
        RetryRoundKind? retryRoundKind = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var retryMessage = message.Trim();
        if (string.IsNullOrWhiteSpace(retryMessage))
        {
            throw new ArgumentException("Retry message cannot be empty.", nameof(message));
        }

        if (task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true })
        {
            throw new InvalidOperationException($"Task '{taskId}' is already running; refresh or cancel it before retrying.");
        }

        if (task.Status == WorkTaskStatus.WaitingForHuman ||
            _humanInputRequests.Values.Any(request => request.GoalId == goalId && request.TaskId == taskId && !request.IsCompleted))
        {
            throw new InvalidOperationException($"Task '{taskId}' is waiting for human input; answer it before retrying.");
        }

        if (invalidateDownstream)
        {
            EnsureNoRunningDownstreamTasks(goal, task);
        }

        var retryAt = _clock.UtcNow;
        var priorCandidate = task.LastDispatch?.ResultCommit;
        ResetTaskForRetry(task, retryAt, retryRoundKind);
        RecordEffectiveAcceptanceCriteriaCorrections(goal, taskId, ProgressKind.TaskRetried, retryMessage);
        Append(goal, taskId, ProgressKind.TaskRetried, retryMessage);
        if (invalidateDownstream)
        {
            InvalidateDownstreamTasks(goal, task, retryAt, priorCandidate);
        }
        ReopenAcceptanceFailedGoalWithRetry(goal, task, $"Retry cleared failed acceptance gate because task {task.Id.Value[..8]} is dispatchable.");
        ReopenTerminalGoalWithNonTerminalTasks(goal, $"Retry reopened goal because task {task.Id.Value[..8]} is dispatchable.");
        RefreshGoalStatus(goal);
        return task;
    }

    public bool NormalizeGoalLifecycleState(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        return ReopenTerminalGoalWithNonTerminalTasks(goal, reason);
    }

    public bool ReopenVerifyingGoalAfterAcceptanceAttemptInvalidated(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        var reopenReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reopenReason))
        {
            throw new ArgumentException("Acceptance invalidation reason cannot be empty.", nameof(reason));
        }

        if (goal.Status != GoalStatus.Verifying ||
            goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
        {
            return false;
        }

        goal.SetStatus(GoalStatus.Active);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reopenReason);
        return true;
    }

    public bool NormalizePrematureCompletedGoalToVerified(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        if (goal.Status != GoalStatus.Completed)
        {
            return false;
        }

        if (!goal.Tasks.All(task => BuildTaskVerificationGate(goal, task).GateStatus == VerificationGateStatus.Passed))
        {
            return false;
        }

        goal.SetStatus(GoalStatus.Verified);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public bool ReconcileGoalVerificationStatus(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        if (goal.Status is GoalStatus.Verifying or GoalStatus.Verified or GoalStatus.Parked or GoalStatus.WaitingForHuman)
        {
            return false;
        }

        if (IsTerminalGoalStatus(goal.Status) && goal.Status != GoalStatus.Completed)
        {
            return false;
        }

        if (_humanInputRequests.Values.Any(candidate => candidate.GoalId == goal.Id && !candidate.IsCompleted))
        {
            return false;
        }

        if (!goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed))
        {
            return false;
        }

        if (!goal.Tasks.All(task => BuildTaskVerificationGate(goal, task).GateStatus == VerificationGateStatus.Passed))
        {
            return false;
        }

        goal.SetStatus(GoalStatus.Verified);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public bool BeginGoalAcceptanceVerification(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        if (goal.Status == GoalStatus.Verifying)
        {
            return false;
        }

        if (goal.Status != GoalStatus.Verified)
        {
            throw new InvalidOperationException($"Goal '{goalId}' is {goal.Status}; only Verified goals can start acceptance verification.");
        }

        if (!goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) ||
            !goal.Tasks.All(task => BuildTaskVerificationGate(goal, task).GateStatus == VerificationGateStatus.Passed))
        {
            throw new InvalidOperationException($"Goal '{goalId}' cannot enter Verifying until all task verification gates have passed.");
        }

        goal.SetStatus(GoalStatus.Verifying);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public bool ReconcileGoalAcceptanceVerified(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        if (goal.Status != GoalStatus.Verifying)
        {
            return false;
        }

        goal.ClearAcceptanceFailure();
        goal.SetStatus(GoalStatus.Verified);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public bool ReconcileGoalAcceptanceFailed(
        GoalId goalId,
        IReadOnlyList<string> failedChecks,
        string reason,
        string? branchHeadSha = null,
        string? mainHeadSha = null,
        IReadOnlyList<AcceptanceCheckAttribution>? checkAttributions = null,
        string? baselineAttestation = null)
    {
        var goal = GetGoal(goalId);
        if (goal.Status != GoalStatus.Verifying)
        {
            return false;
        }

        goal.RecordAcceptanceFailure(
            failedChecks,
            _clock.UtcNow,
            branchHeadSha,
            mainHeadSha,
            checkAttributions,
            baselineAttestation);
        goal.SetStatus(GoalStatus.AcceptanceFailed);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public TaskSpec RequeueInterruptedDispatch(
        GoalId goalId,
        TaskId taskId,
        string message,
        string? interruptedDispatchId = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var retryMessage = message.Trim();
        if (string.IsNullOrWhiteSpace(retryMessage))
        {
            throw new ArgumentException("Retry message cannot be empty.", nameof(message));
        }

        if (task.Status == WorkTaskStatus.WaitingForHuman ||
            _humanInputRequests.Values.Any(request => request.GoalId == goalId && request.TaskId == taskId && !request.IsCompleted))
        {
            throw new InvalidOperationException($"Task '{taskId}' is waiting for human input; answer it before retrying.");
        }

        task.ClearLatestVerification();
        task.ClearLastExecution();
        task.ClearLastDispatch();
        task.ClearLastProcess();
        task.ClearSubscriptionRetryAfter();
        task.RecordRetry(_clock.UtcNow);
        task.SetInterruptedDispatchRecovery(interruptedDispatchId);
        task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
        Append(goal, taskId, ProgressKind.TaskRetried, retryMessage);
        ReopenTerminalGoalWithNonTerminalTasks(goal, $"Interrupted dispatch recovery reopened goal because task {task.Id.Value[..8]} is dispatchable.");
        RefreshGoalStatus(goal);
        return task;
    }

    public int RecordCriterionRetryFeedback(GoalId goalId, TaskId taskId, IReadOnlyList<string> feedback)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.RecordCriterionRetryFeedback(feedback);
        task.IncrementCriterionRetryCount();
        goal.IncrementAutomaticAcceptanceRetryCount();
        return goal.AutomaticAcceptanceRetryCount;
    }

    public void RecordAcceptanceFailure(
        GoalId goalId,
        IReadOnlyList<string> failedChecks,
        string? branchHeadSha = null,
        string? mainHeadSha = null,
        IReadOnlyList<AcceptanceCheckAttribution>? checkAttributions = null,
        string? baselineAttestation = null)
    {
        var goal = GetGoal(goalId);
        goal.RecordAcceptanceFailure(
            failedChecks,
            _clock.UtcNow,
            branchHeadSha,
            mainHeadSha,
            checkAttributions,
            baselineAttestation);
    }

    public void ClearAcceptanceFailure(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        goal.ClearAcceptanceFailure();
    }

    public int RetryAcceptanceGate(GoalId goalId, string operatorReason)
    {
        var goal = GetGoal(goalId);
        var reason = ValidateAcceptanceGateRetry(goal, operatorReason);

        goal.ClearAcceptanceFailure();
        goal.ResetAutomaticAcceptanceRetryCount();
        var operatorRegateCount = goal.IncrementOperatorAcceptanceRegateCount();
        goal.SetStatus(GoalStatus.Verified);
        Append(
            goal,
            null,
            ProgressKind.GoalPolicyDecision,
            $"Operator requested acceptance re-gate ({operatorRegateCount}/{Goal.OperatorAcceptanceRegateCap}): {reason}");
        return operatorRegateCount;
    }

    public void ValidateAcceptanceGateRetry(GoalId goalId, string operatorReason) =>
        ValidateAcceptanceGateRetry(GetGoal(goalId), operatorReason);

    private static string ValidateAcceptanceGateRetry(Goal goal, string operatorReason)
    {
        var reason = operatorReason.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Acceptance retry reason cannot be empty.", nameof(operatorReason));
        }

        var isVerifiedApparatusHold =
            goal.Status == GoalStatus.Verified &&
            goal.LatestAcceptanceFailure is { IsEnvironmentalApparatus: true };
        if (goal.Status != GoalStatus.AcceptanceFailed && !isVerifiedApparatusHold)
        {
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' is {goal.Status}, not AcceptanceFailed or a typed Verified apparatus hold; " +
                "acceptance-retry is only valid after a failed acceptance gate.");
        }

        var incompleteTask = goal.Tasks.FirstOrDefault(task =>
            task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled));
        if (incompleteTask is not null)
        {
            var taskNumber = TaskDisplayNumber.Resolve(goal, incompleteTask.Id);
            var correctLever = incompleteTask.Status switch
            {
                WorkTaskStatus.Failed => $"retry {goal.Id.Value[..8]} {taskNumber} <reason>",
                WorkTaskStatus.WaitingForHuman => "answer <request-id> <answer>",
                WorkTaskStatus.Running => $"refresh-dispatch {goal.Id.Value[..8]} {taskNumber}",
                _ => $"conduct {goal.Id.Value[..8]} --loop"
            };
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' task {taskNumber} is {incompleteTask.Status}, not Completed or Cancelled; use '{correctLever}' instead of acceptance-retry.");
        }

        var unverifiedTask = goal.Tasks.FirstOrDefault(task =>
            task.Status == WorkTaskStatus.Completed &&
            task.LastVerification is not { Succeeded: true });
        if (unverifiedTask is not null)
        {
            var taskNumber = TaskDisplayNumber.Resolve(goal, unverifiedTask.Id);
            var verificationState = unverifiedTask.LastVerification is null
                ? "has no verification"
                : "latest verification did not pass";
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' task {taskNumber} is Completed but {verificationState}; " +
                $"use 'verify-manual {goal.Id.Value[..8]} {taskNumber} passed <note>' after validating the work, " +
                "or retry the task if the work is wrong.");
        }

        if (goal.OperatorAcceptanceRegateCount >= Goal.OperatorAcceptanceRegateCap)
        {
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value[..8]}' has reached the acceptance-retry cap of {Goal.OperatorAcceptanceRegateCap} operator re-gates; inspect and repair the persistent gate failure before retrying.");
        }

        return reason;
    }

    public void ClearCriterionRetryFeedback(GoalId goalId, TaskId taskId)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.ClearCriterionRetryFeedback();
    }

    public TaskSpec RedelegateTask(GoalId goalId, TaskId taskId, IReadOnlyList<AgentDefinition> availableAgents)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true })
        {
            throw new InvalidOperationException($"Task '{taskId}' is running; cancel or refresh it before re-delegating.");
        }

        if (task.Status is not (WorkTaskStatus.Assigned or WorkTaskStatus.Failed))
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; only Assigned or Failed tasks can be re-delegated.");
        }

        var currentAgentId = task.AssignedAgentId;
        var agent = availableAgents.FirstOrDefault(candidate =>
            candidate.Status == AgentStatus.Available &&
            candidate.Role == task.RequiredRole &&
            candidate.Id != currentAgentId) ??
            availableAgents.FirstOrDefault(candidate =>
                candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole);
        if (agent is null)
        {
            throw new KeyNotFoundException($"No available {task.RequiredRole} agent was found for task '{taskId}'.");
        }

        var previousAgentId = task.AssignedAgentId?.Value ?? "none";
        task.AssignTo(agent.Id);
        Append(
            goal,
            task.Id,
            ProgressKind.TaskRedelegated,
            $"Re-delegated {task.RequiredRole} task from agent '{previousAgentId}' to agent '{agent.Id.Value}' ({agent.Name}).");
        RefreshGoalStatus(goal);
        return task;
    }

    public TaskSpec ReassignTaskAgent(GoalId goalId, TaskId taskId, AgentDefinition agent, string? reason = null)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (agent.Role != task.RequiredRole)
        {
            throw new InvalidOperationException($"Agent '{agent.Id.Value}' has role {agent.Role}; task '{taskId}' requires {task.RequiredRole}.");
        }

        var previousAgentId = task.AssignedAgentId?.Value ?? "none";
        task.AssignTo(agent.Id);
        Append(
            goal,
            task.Id,
            ProgressKind.TaskRedelegated,
            string.IsNullOrWhiteSpace(reason)
                ? $"Reassigned {task.RequiredRole} task from agent '{previousAgentId}' to agent '{agent.Id.Value}' ({agent.Name})."
                : reason.Trim());
        RefreshGoalStatus(goal);
        return task;
    }

    public TaskSpec AcknowledgeSubscriptionLimitReview(GoalId goalId, TaskId taskId, string note)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var reviewNote = note.Trim();
        if (string.IsNullOrWhiteSpace(reviewNote))
        {
            throw new ArgumentException("Subscription limit review note cannot be empty.", nameof(note));
        }

        var failureCount = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        if (failureCount < DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold)
        {
            throw new InvalidOperationException($"Task '{taskId}' does not have repeated recoverable subscription usage-limit failures to review.");
        }

        task.RecordSubscriptionLimitReview(reviewNote, _clock.UtcNow, failureCount);
        if (task.Status == WorkTaskStatus.Failed &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } latest &&
            DispatchFailureClassifier.IsRecoverableSubscriptionLimitFailure(latest))
        {
            task.ClearLatestVerification();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
        }

        Append(
            goal,
            taskId,
            ProgressKind.TaskSubscriptionLimitReviewAcknowledged,
            $"Subscription usage-limit review acknowledged after {failureCount} recoverable failure(s): {reviewNote}");
        RefreshGoalStatus(goal);
        return task;
    }

    public Goal CancelGoal(GoalId goalId, string reason) => StopGoal(goalId, GoalStatus.Cancelled, reason);

    public Goal SupersedeGoal(GoalId goalId, string reason) => StopGoal(goalId, GoalStatus.Superseded, reason);

    public Goal ParkGoal(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        var parkReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parkReason))
        {
            throw new ArgumentException("Goal park reason cannot be empty.", nameof(reason));
        }

        if (goal.Status == GoalStatus.Completed)
        {
            throw new InvalidOperationException($"Goal '{goalId}' is Completed and cannot be parked.");
        }

        if (IsTerminalGoalStatus(goal.Status))
        {
            throw new InvalidOperationException($"Goal '{goalId}' is already {goal.Status}.");
        }

        goal.SetStatus(GoalStatus.Parked);
        var resolution = BuildGoalParkResolution(parkReason);
        CompleteOpenHumanInputRequestsForGoal(goal.Id, resolution);
        Append(goal, null, ProgressKind.GoalPolicyDecision, resolution);
        return goal;
    }

    public Goal UnparkGoal(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        var unparkReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(unparkReason))
        {
            throw new ArgumentException("Goal unpark reason cannot be empty.", nameof(reason));
        }

        if (goal.Status != GoalStatus.Parked)
        {
            throw new InvalidOperationException(
                $"unpark-goal only applies to Parked goals; goal '{goalId}' is {goal.Status}. " +
                "No state changed. Use status <goal> to inspect the current lifecycle state.");
        }

        goal.SetStatus(GoalStatus.Active);
        Append(goal, null, ProgressKind.GoalPolicyDecision, $"Goal unparked: {unparkReason}");
        return goal;
    }

    public int RefreshParkedGoalsWithResolvedHumanWaits()
    {
        var promoted = 0;
        foreach (var goal in _goals.Values.Where(goal => goal.Status == GoalStatus.Parked).ToArray())
        {
            if (_humanInputRequests.Values.Any(request => request.GoalId == goal.Id && !request.IsCompleted) ||
                !HasHumanInputResolvedAfterLatestParkDecision(goal))
            {
                continue;
            }

            RestoreTasksAfterResolvedHumanInput(goal);
            RefreshGoalStatus(goal, allowParkedRefresh: true);
            if (goal.Status != GoalStatus.Parked)
            {
                promoted++;
                Append(goal, null, ProgressKind.GoalPolicyDecision, "Goal unparked: resolved parked human wait.");
            }
        }

        return promoted;
    }

    public Goal CompleteGoal(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        var completeReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(completeReason))
        {
            throw new ArgumentException("Goal completion reason cannot be empty.", nameof(reason));
        }

        if (goal.Status == GoalStatus.Completed)
        {
            return goal;
        }

        if (goal.Status is not (GoalStatus.Verifying or GoalStatus.Verified))
        {
            throw new InvalidOperationException($"Goal '{goalId}' is {goal.Status}; only Verifying or Verified goals can be completed.");
        }

        if (goal.Status == GoalStatus.Verifying &&
            !goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
        {
            throw new InvalidOperationException($"Goal '{goalId}' cannot complete from Verifying until every task is terminal.");
        }

        goal.SetStatus(GoalStatus.Completed);
        Append(goal, null, ProgressKind.GoalPolicyDecision, completeReason);
        return goal;
    }

    public Goal CompleteGoalFromMergeEvidence(GoalId goalId, string integrateSha, string reason)
    {
        var goal = GetGoal(goalId);
        var normalizedIntegrateSha = integrateSha?.Trim() ?? string.Empty;
        var completeReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedIntegrateSha))
        {
            throw new ArgumentException("Integrate commit SHA cannot be empty.", nameof(integrateSha));
        }

        if (string.IsNullOrWhiteSpace(completeReason))
        {
            throw new ArgumentException("Goal completion reason cannot be empty.", nameof(reason));
        }

        if (goal.Status == GoalStatus.Completed)
        {
            return goal;
        }

        if (IsTerminalGoalStatus(goal.Status))
        {
            throw new InvalidOperationException(
                $"Goal '{goalId}' is already terminal as {goal.Status}; merge evidence cannot rewrite that terminal outcome.");
        }

        if (goal.Tasks.Any(task =>
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed) ||
                task.LastProcess is { IsRunning: true }))
        {
            throw new InvalidOperationException(
                $"Goal '{goalId}' cannot complete from merge evidence while any task is non-terminal or has a live process.");
        }

        foreach (var task in goal.Tasks.Where(task => task.Status is WorkTaskStatus.Cancelled or WorkTaskStatus.Failed))
        {
            Append(
                goal,
                task.Id,
                ProgressKind.GoalPolicyDecision,
                $"MERGE_EVIDENCE_CONFLICT task={task.Id.Value} status={task.Status} " +
                $"terminalization=landed integrateSha={normalizedIntegrateSha}");
        }

        if (goal.Status is not (GoalStatus.Verifying or GoalStatus.Verified))
        {
            Append(
                goal,
                null,
                ProgressKind.GoalPolicyDecision,
                $"MERGE_EVIDENCE_CONFLICT goalStatus={goal.Status} terminalization=landed " +
                $"integrateSha={normalizedIntegrateSha}");
        }

        goal.SetStatus(GoalStatus.Completed);
        Append(goal, null, ProgressKind.GoalPolicyDecision, completeReason);
        return goal;
    }

    public void RecordGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha)
    {
        _eventWriter.AppendGoalLandedFromAncestry(
            goalId,
            goalBranch,
            branchTip,
            mainSha);
    }

    public void RecordGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha)
    {
        _eventWriter.AppendGoalLandedFromMergeEvidence(
            goalId,
            goalBranch,
            integrateSha,
            mainSha);
    }

    private Goal StopGoal(GoalId goalId, GoalStatus terminalStatus, string reason)
    {
        var goal = GetGoal(goalId);
        var stopReason = reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stopReason))
        {
            throw new ArgumentException("Goal stop reason cannot be empty.", nameof(reason));
        }

        if (goal.Status == GoalStatus.Completed)
        {
            throw new InvalidOperationException($"Goal '{goalId}' is Completed and cannot be cancelled or superseded.");
        }

        if (IsTerminalGoalStatus(goal.Status))
        {
            throw new InvalidOperationException($"Goal '{goalId}' is already {goal.Status}.");
        }

        var runningTasks = goal.Tasks
            .Where(task => task.LastProcess is { IsRunning: true })
            .ToList();
        if (runningTasks.Count > 0)
        {
            var taskList = string.Join(", ", runningTasks.Select(task => $"{task.Id.Value[..8]} pid={task.LastProcess!.ProcessId}"));
            throw new InvalidOperationException($"Goal '{goalId}' has running dispatch process(es): {taskList}. Cancel or refresh them before stopping the goal.");
        }

        goal.SetStatus(terminalStatus);
        Append(
            goal,
            null,
            terminalStatus == GoalStatus.Superseded ? ProgressKind.GoalSuperseded : ProgressKind.GoalCancelled,
            stopReason);
        return goal;
    }

    public void ReportTaskProgress(GoalId goalId, TaskId taskId, WorkTaskStatus status, string message)
    {
        if (status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.WaitingForHuman)
        {
            throw new ArgumentException($"Status '{status}' is not a reportable agent progress state.", nameof(status));
        }

        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetStatus(status);

        var kind = status switch
        {
            WorkTaskStatus.Running => ProgressKind.TaskStarted,
            WorkTaskStatus.Completed => ProgressKind.TaskCompleted,
            WorkTaskStatus.Failed => ProgressKind.TaskFailed,
            WorkTaskStatus.Cancelled => ProgressKind.TaskCancelled,
            _ => ProgressKind.TaskUpdated
        };

        RecordEffectiveAcceptanceCriteriaCorrections(goal, taskId, kind, message);
        Append(goal, taskId, kind, message);
        if (status is WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
        {
            ReconcileRetainedDownstreamTasks(goal, task, _clock.UtcNow);
        }

        RefreshGoalStatus(goal);
    }

    public void EscalateTaskFailure(GoalId goalId, TaskId taskId, string message)
    {
        var escalationMessage = message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(escalationMessage))
        {
            throw new ArgumentException("Escalation message cannot be empty.", nameof(message));
        }

        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        if (task.Status is WorkTaskStatus.Running || task.LastProcess is { IsRunning: true })
        {
            throw new InvalidOperationException($"Task '{taskId}' is still running and cannot be escalated.");
        }

        task.SetStatus(WorkTaskStatus.Failed);
        RecordEffectiveAcceptanceCriteriaCorrections(goal, taskId, ProgressKind.TaskFailed, escalationMessage);
        Append(goal, taskId, ProgressKind.TaskFailed, escalationMessage);
        goal.SetStatus(GoalStatus.Failed);
        Append(
            goal,
            null,
            ProgressKind.GoalPolicyDecision,
            $"Goal failed because task {task.Id.Value[..8]} reached an explicit terminal escalation.");
    }

    private static void ResetTaskForRetry(TaskSpec task, DateTimeOffset retryAt, RetryRoundKind? retryRoundKind = null)
    {
        task.ClearLatestVerification();
        task.ClearLastExecution();
        task.ClearLastDispatch();
        task.ClearLastProcess();
        task.ClearSubscriptionRetryAfter();
        task.SetInterruptedDispatchRecovery(null);
        task.RecordRetry(retryAt, retryRoundKind);
        task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
    }

    private bool ReopenTerminalGoalWithNonTerminalTasks(Goal goal, string reason)
    {
        if (!IsTerminalGoalStatus(goal.Status) ||
            goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
        {
            return false;
        }

        goal.SetStatus(GoalStatus.Active);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    private void ReopenAcceptanceFailedGoalWithRetry(Goal goal, TaskSpec retriedTask, string reason)
    {
        if (goal.Status != GoalStatus.AcceptanceFailed ||
            retriedTask.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
        {
            return;
        }

        goal.ClearAcceptanceFailure();
        goal.SetStatus(GoalStatus.Active);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
    }

    private void InvalidateDownstreamTasks(
        Goal goal,
        TaskSpec retriedTask,
        DateTimeOffset retryAt,
        string? priorCandidate)
    {
        foreach (var downstream in goal.Tasks.Where(task => IsDownstreamRole(retriedTask.RequiredRole, task.RequiredRole)))
        {
            if (downstream.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned &&
                downstream.LastVerification is null &&
                downstream.LastExecution is null &&
                downstream.LastDispatch is null &&
                downstream.LastProcess is null &&
                downstream.SubscriptionRetryAfter is null)
            {
                continue;
            }

            if (downstream.Status == WorkTaskStatus.Completed &&
                downstream.LastVerification is not null &&
                SameNonEmptyReviewedCommit(downstream.LastVerification.ReviewedCommit, priorCandidate))
            {
                Append(
                    goal,
                    downstream.Id,
                    ProgressKind.TaskUpdated,
                    $"Preserved {downstream.RequiredRole} task because its verification covers candidate {priorCandidate!.Trim()} while upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} is retried.");
                continue;
            }

            ResetTaskForRetry(downstream, retryAt);
            Append(
                goal,
                downstream.Id,
                ProgressKind.TaskRetried,
                $"Invalidated {downstream.RequiredRole} task because upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} was retried.");
        }
    }

    private void ReconcileRetainedDownstreamTasks(Goal goal, TaskSpec retriedTask, DateTimeOffset reconciledAt)
    {
        if (retriedTask.LatestRetryAt is null)
        {
            return;
        }

        var currentCandidateSha = retriedTask.Status == WorkTaskStatus.Completed
            ? retriedTask.LastDispatch?.ResultCommit
            : null;
        var currentCandidateKnown = !string.IsNullOrWhiteSpace(currentCandidateSha);
        var currentCandidate = !currentCandidateKnown
            ? "unknown"
            : currentCandidateSha!.Trim();
        foreach (var downstream in goal.Tasks.Where(task =>
                     IsDownstreamRole(retriedTask.RequiredRole, task.RequiredRole) &&
                     task.Status == WorkTaskStatus.Completed &&
                     task.LastVerification is not null &&
                     !SameNonEmptyReviewedCommit(task.LastVerification.ReviewedCommit, currentCandidateSha)))
        {
            var reviewedCandidate = string.IsNullOrWhiteSpace(downstream.LastVerification!.ReviewedCommit)
                ? "unknown"
                : downstream.LastVerification.ReviewedCommit.Trim();
            var invalidationReason = retriedTask.Status == WorkTaskStatus.Completed && currentCandidateKnown
                ? $"changed candidate from {reviewedCandidate} to {currentCandidate}"
                : $"did not prove candidate {reviewedCandidate} unchanged (result {currentCandidate}; status {retriedTask.Status})";
            ResetTaskForRetry(downstream, reconciledAt);
            Append(
                goal,
                downstream.Id,
                ProgressKind.TaskRetried,
                $"Invalidated {downstream.RequiredRole} task because retried upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} {invalidationReason}.");
        }
    }

    private static void EnsureNoRunningDownstreamTasks(Goal goal, TaskSpec retriedTask)
    {
        var runningDownstream = goal.Tasks.FirstOrDefault(task =>
            IsDownstreamRole(retriedTask.RequiredRole, task.RequiredRole) &&
            task.LastProcess is { IsRunning: true });
        if (runningDownstream is not null)
        {
            throw new InvalidOperationException(
                $"Cannot retry {retriedTask.RequiredRole} task '{retriedTask.Id}' while downstream {runningDownstream.RequiredRole} task '{runningDownstream.Id}' has a running process.");
        }
    }

    private static bool IsDownstreamRole(AgentRole upstream, AgentRole candidate) =>
        SdlcRoleOrder(candidate) > SdlcRoleOrder(upstream);

    private static int SdlcRoleOrder(AgentRole role) => role switch
    {
        AgentRole.Researcher => 0,
        AgentRole.Planner => 1,
        AgentRole.Ideation => 2,
        AgentRole.Developer => 3,
        AgentRole.Tester => 4,
        AgentRole.Reviewer => 5,
        _ => int.MaxValue
    };

    public HumanInputRequest RequestHumanInput(
        GoalId goalId,
        TaskId? taskId,
        string question,
        HumanWaitKind kind = HumanWaitKind.SpecClarification,
        bool? isAutoDefaultable = null,
        bool? isDismissible = null,
        bool isAnswerRequired = true,
        bool? isExternallyBlocked = null,
        string? suggestedDefaultAnswer = null,
        string? resumeCommand = null)
    {
        return RequestHumanInputDeduplicated(
            goalId,
            taskId,
            question,
            kind,
            isAutoDefaultable,
            isDismissible,
            isAnswerRequired,
            isExternallyBlocked,
            suggestedDefaultAnswer,
            resumeCommand).Request;
    }

    public HumanInputRequestCreationResult RequestHumanInputDeduplicated(
        GoalId goalId,
        TaskId? taskId,
        string question,
        HumanWaitKind kind = HumanWaitKind.SpecClarification,
        bool? isAutoDefaultable = null,
        bool? isDismissible = null,
        bool isAnswerRequired = true,
        bool? isExternallyBlocked = null,
        string? suggestedDefaultAnswer = null,
        string? resumeCommand = null,
        string? questionFingerprint = null,
        string? blockerFingerprint = null,
        int? completedRound = null,
        string? workerResultLogReference = null,
        bool recordDuplicateSuppression = true)
    {
        var goal = GetGoal(goalId);
        if (taskId is not null)
        {
            goal.FindTask(taskId);
        }

        var effectiveQuestionFingerprint = string.IsNullOrWhiteSpace(questionFingerprint)
            ? HumanInputRequest.BuildQuestionFingerprint(question)
            : questionFingerprint.Trim();

        lock (_humanInputRequestLock)
        {
            var matches = _humanInputRequests.Values
                .Where(candidate =>
                    candidate.GoalId == goalId &&
                    candidate.TaskId == taskId &&
                    string.Equals(candidate.QuestionFingerprint, effectiveQuestionFingerprint, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.RequestedAt)
                .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
                .ToList();
            var open = matches.FirstOrDefault(candidate => !candidate.IsCompleted);
            if (open is not null)
            {
                if (recordDuplicateSuppression && completedRound is not null)
                {
                    ResetAnsweredHumanInputSuppressionStreaks(goalId, taskId, exceptRequestId: null);
                }

                HoldTaskForExistingHumanInput(goal, taskId);
                if (recordDuplicateSuppression)
                {
                    open.IncrementSuppressionCount();
                    AppendDuplicateHumanInputSuppressed(
                        goal,
                        taskId,
                        open,
                        answered: false,
                        completedRound,
                        workerResultLogReference);
                }

                return new HumanInputRequestCreationResult(open, WasReused: true, WasSuppressedByAnswer: false);
            }

            if (!string.IsNullOrWhiteSpace(blockerFingerprint))
            {
                var answered = matches.FirstOrDefault(candidate =>
                    candidate.IsCompleted &&
                    !candidate.WasDismissed &&
                    !candidate.IsSyntheticParkedHumanWaitCompletion &&
                    !string.IsNullOrWhiteSpace(candidate.Answer) &&
                    string.Equals(candidate.BlockerFingerprint, blockerFingerprint, StringComparison.Ordinal));
                if (answered is not null)
                {
                    if (!recordDuplicateSuppression)
                    {
                        return new HumanInputRequestCreationResult(answered, WasReused: false, WasSuppressedByAnswer: true);
                    }

                    if (completedRound is not null)
                    {
                        ResetAnsweredHumanInputSuppressionStreaks(goalId, taskId, answered.Id);
                    }
                    answered.IncrementSuppressionCount();
                    RestoreTaskAfterSuppressedAnsweredInput(
                        goal,
                        taskId,
                        answered,
                        completedRound,
                        workerResultLogReference);
                    AppendDuplicateHumanInputSuppressed(
                        goal,
                        taskId,
                        answered,
                        answered: true,
                        completedRound,
                        workerResultLogReference);
                    return new HumanInputRequestCreationResult(answered, WasReused: false, WasSuppressedByAnswer: true);
                }
            }

            if (completedRound is not null)
            {
                ResetAnsweredHumanInputSuppressionStreaks(goalId, taskId, exceptRequestId: null);
            }
            var id = HumanInputRequestId.New();
            var request = new HumanInputRequest(
                id,
                goal.Id,
                taskId,
                question,
                _clock.UtcNow,
                kind,
                isAutoDefaultable,
                isDismissible,
                isAnswerRequired,
                isExternallyBlocked,
                suggestedDefaultAnswer,
                resumeCommand ?? HumanInputRequest.BuildDefaultResumeCommand(id),
                effectiveQuestionFingerprint,
                blockerFingerprint);
            _humanInputRequests.Add(request.Id, request);

            HoldTaskForExistingHumanInput(goal, taskId);
            Append(goal, taskId, ProgressKind.HumanInputRequested, question);
            AppendContradictoryRecordAdvisoryIfNeeded(goal, request);
            return new HumanInputRequestCreationResult(request, WasReused: false, WasSuppressedByAnswer: false);
        }
    }

    private void AppendContradictoryRecordAdvisoryIfNeeded(Goal goal, HumanInputRequest request)
    {
        var matches = _humanInputRequests.Values
            .Where(candidate =>
                candidate.GoalId == goal.Id &&
                string.Equals(candidate.QuestionFingerprint, request.QuestionFingerprint, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.RequestedAt)
            .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var distinctTaskIds = matches
            .Where(candidate => candidate.TaskId is not null)
            .Select(candidate => candidate.TaskId!)
            .Distinct()
            .ToArray();
        if (distinctTaskIds.Length < DuplicateHumanInputDistinctTaskThreshold)
        {
            return;
        }

        var marker = $"questionFingerprint={request.QuestionFingerprint}";
        var priorSignal = goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.ContradictoryRecordDetected &&
                evt.Message.Contains(marker, StringComparison.Ordinal))
            .OrderByDescending(evt => evt.OccurredAt)
            .FirstOrDefault();
        if (priorSignal is not null && !matches.Any(candidate => candidate.AnsweredAt > priorSignal.OccurredAt))
        {
            return;
        }

        var roles = distinctTaskIds
            .Select(taskId => goal.FindTask(taskId).RequiredRole)
            .Distinct()
            .OrderBy(role => role)
            .ToArray();
        var totalOccurrences = matches.Sum(candidate => 1 + candidate.SuppressionCount);
        var answeredRequest = matches
            .Where(candidate => candidate.IsCompleted && !candidate.WasDismissed)
            .OrderByDescending(candidate => candidate.AnsweredAt)
            .FirstOrDefault();
        Append(
            goal,
            null,
            ProgressKind.ContradictoryRecordDetected,
            $"kind=contradictory-record {marker} distinctTasks={distinctTaskIds.Length} " +
            $"totalOccurrences={totalOccurrences} tasks={string.Join(',', distinctTaskIds.Select(id => id.Value))} " +
            $"roles={string.Join(',', roles)} requests={string.Join(',', matches.Select(candidate => candidate.Id.Value))} " +
            $"alreadyAnsweredRequest={answeredRequest?.Id.Value ?? "none"}");
    }

    private void HoldTaskForExistingHumanInput(Goal goal, TaskId? taskId)
    {
        if (taskId is not null)
        {
            goal.FindTask(taskId).SetStatus(WorkTaskStatus.WaitingForHuman);
        }

        goal.SetStatus(GoalStatus.WaitingForHuman);
    }

    private void RestoreTaskAfterSuppressedAnsweredInput(
        Goal goal,
        TaskId? taskId,
        HumanInputRequest request,
        int? completedRound,
        string? workerResultLogReference)
    {
        if (taskId is not null)
        {
            var task = goal.FindTask(taskId);
            if (request.SuppressionCount >= DuplicateHumanInputSuppressionThreshold)
            {
                if (task.Status != WorkTaskStatus.Completed)
                {
                    task.SetStatus(WorkTaskStatus.Failed);

                    if (request.SuppressionCount == DuplicateHumanInputSuppressionThreshold)
                    {
                        Append(
                            goal,
                            taskId,
                            ProgressKind.TaskFailed,
                            $"Answered human-input request {request.Id.Value[..8]} was genuinely re-raised by " +
                            $"completed worker round={completedRound?.ToString() ?? "unknown"}; " +
                            $"worker_result_log={FormatWorkerResultLogReference(workerResultLogReference)}; suppression threshold " +
                            $"{DuplicateHumanInputSuppressionThreshold} reached. Automatic redispatch stopped.");
                    }

                }
            }
            else if (task.Status != WorkTaskStatus.Completed)
            {
                task.SetStatus(WorkTaskStatus.Assigned);
            }
        }

        RefreshGoalStatus(goal);
    }

    private void AppendDuplicateHumanInputSuppressed(
        Goal goal,
        TaskId? taskId,
        HumanInputRequest request,
        bool answered,
        int? completedRound = null,
        string? workerResultLogReference = null)
    {
        var thresholdReceipt = request.SuppressionCount == DuplicateHumanInputSuppressionThreshold
            ? "; repeated suppression threshold reached"
            : string.Empty;
        Append(
            goal,
            taskId,
            ProgressKind.DuplicateHumanInputSuppressed,
            $"kind=duplicate-human-input-suppressed request={request.Id.Value[..8]} " +
            $"count={request.SuppressionCount} state={(answered ? "answered" : "open")} " +
            $"round={completedRound?.ToString() ?? "unknown"} " +
            $"worker_result_log={FormatWorkerResultLogReference(workerResultLogReference)}{thresholdReceipt}");
    }

    private void ResetAnsweredHumanInputSuppressionStreaks(
        GoalId goalId,
        TaskId? taskId,
        HumanInputRequestId? exceptRequestId)
    {
        foreach (var request in _humanInputRequests.Values.Where(candidate =>
                     candidate.GoalId == goalId &&
                     candidate.TaskId == taskId &&
                     candidate.IsCompleted &&
                     !candidate.WasDismissed &&
                     candidate.Id != exceptRequestId))
        {
            request.ResetSuppressionCount();
        }
    }

    internal void ResetAnsweredHumanInputSuppressionStreaks(GoalId goalId, TaskId taskId)
    {
        lock (_humanInputRequestLock)
        {
            ResetAnsweredHumanInputSuppressionStreaks(goalId, taskId, exceptRequestId: null);
        }
    }

    private static string FormatWorkerResultLogReference(string? workerResultLogReference) =>
        string.IsNullOrWhiteSpace(workerResultLogReference)
            ? "unavailable"
            : workerResultLogReference.Trim();

    public void SubmitHumanInput(
        HumanInputRequestId requestId,
        string answer,
        IReadOnlyList<string>? gatedDeliverableIds = null)
    {
        lock (_humanInputRequestLock)
        {
            if (!_humanInputRequests.TryGetValue(requestId, out var request))
            {
                throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
            }

            if (request.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"Human input request '{requestId}' has already been answered — use supersede to correct it.");
            }

            var goal = GetGoal(request.GoalId);
            var answeredAt = _clock.UtcNow;
            request.Complete(
                answer,
                answeredAt,
                gatedDeliverableIds,
                briefVersion: goal.AuthoritativeBrief.Version);
            var siblings = _humanInputRequests.Values
                .Where(candidate =>
                    candidate.Id != request.Id &&
                    candidate.GoalId == request.GoalId &&
                    candidate.TaskId == request.TaskId &&
                    !candidate.IsCompleted &&
                    string.Equals(candidate.QuestionFingerprint, request.QuestionFingerprint, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.RequestedAt)
                .ToList();
            foreach (var sibling in siblings)
            {
                sibling.CompleteAsSuperseded(
                    answer,
                    answeredAt,
                    request.Id,
                    goal.AuthoritativeBrief.Version);
            }

            if (request.TaskId is not null)
            {
                var task = goal.FindTask(request.TaskId);
                if (!_humanInputRequests.Values.Any(candidate =>
                        candidate.GoalId == goal.Id &&
                        candidate.TaskId == task.Id &&
                        !candidate.IsCompleted))
                {
                    RestoreTaskAfterHumanInput(goal, task);
                }
            }

            RefreshGoalStatus(goal);

            var siblingReceipt = siblings.Count == 0
                ? string.Empty
                : $" Resolved {siblings.Count} sibling duplicate request(s).";
            Append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer + siblingReceipt);
        }
    }

    public HumanInputAnswerRecord SupersedeHumanInput(
        GoalId goalId,
        HumanInputRequestId requestId,
        string replacementAnswer,
        HumanInputAnswerOrigin origin)
    {
        lock (_humanInputRequestLock)
        {
            var goal = GetGoal(goalId);
            if (!_humanInputRequests.TryGetValue(requestId, out var request) || request.GoalId != goal.Id)
            {
                throw new KeyNotFoundException(
                    $"Human input request '{requestId}' was not found on goal '{goal.Id.Value}'.");
            }

            var previousAnswer = request.AuthoritativeAnswer
                ?? throw new InvalidOperationException(
                    $"Human input request '{requestId}' has no answered clarification to supersede.");
            var supersededAt = _clock.UtcNow;
            var matchingRequests = _humanInputRequests.Values
                .Where(candidate =>
                    candidate.GoalId == goal.Id &&
                    candidate.Id != request.Id &&
                    string.Equals(candidate.QuestionFingerprint, request.QuestionFingerprint, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.RequestedAt)
                .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var allMatchingRequests = matchingRequests.Append(request).ToArray();
            var retractionTerms = allMatchingRequests
                .Select(candidate => candidate.DerivedBlockerEvidence)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Select(term => NormalizeRetractionMatch(term!))
                .Where(term => term.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var resolvedStableIds = GetReviewFindingState(goal.Id)
                .Where(finding =>
                    finding.State == ReviewFindingState.Open &&
                    retractionTerms.Any(term =>
                    {
                        var description = NormalizeRetractionMatch(finding.Description);
                        return description.Equals(term, StringComparison.Ordinal);
                    }))
                .Select(finding => finding.StableId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(stableId => stableId, StringComparer.Ordinal)
                .ToArray();
            var thresholdAffectedTaskIds = allMatchingRequests
                .Where(candidate => candidate.SuppressionCount >= DuplicateHumanInputSuppressionThreshold)
                .Where(candidate => candidate.TaskId is not null)
                .Select(candidate => candidate.TaskId!)
                .ToArray();
            var replacement = request.Supersede(
                replacementAnswer,
                supersededAt,
                origin,
                goal.AuthoritativeBrief.Version);
            var resolvedRequests = matchingRequests.Where(candidate => !candidate.IsCompleted).ToArray();
            foreach (var related in resolvedRequests)
            {
                related.CompleteAsSuperseded(
                    replacement.Text,
                    supersededAt,
                    request.Id,
                    goal.AuthoritativeBrief.Version);
            }

            var affectedTasks = resolvedRequests
                .Where(candidate => candidate.TaskId is not null)
                .Select(candidate => candidate.TaskId!)
                .Concat(thresholdAffectedTaskIds)
                .Distinct()
                .Select(goal.FindTask)
                .Where(task => task.Status is WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Failed)
                .ToArray();
            foreach (var task in affectedTasks)
            {
                if (!_humanInputRequests.Values.Any(candidate =>
                        candidate.GoalId == goal.Id &&
                        candidate.TaskId == task.Id &&
                        !candidate.IsCompleted))
                {
                    RestoreTaskAfterHumanInput(goal, task);
                }
            }

            RefreshGoalStatus(goal);
            var clearedBlockerCount = allMatchingRequests
                .Select(candidate => candidate.BlockerFingerprint)
                .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
                .Distinct(StringComparer.Ordinal)
                .Count();
            Append(
                goal,
                request.TaskId,
                ProgressKind.HumanInputSuperseded,
                $"kind=human-input-superseded request={request.Id.Value} replacedAnswer={previousAnswer.Id} " +
                $"authoritativeAnswer={replacement.Id} clearedFindings={clearedBlockerCount} " +
                $"resolvedStableIds={(resolvedStableIds.Length == 0 ? "none" : string.Join(',', resolvedStableIds))} " +
                $"resolvedRequests={resolvedRequests.Length} unblockedTasks={affectedTasks.Length}");
            return replacement;
        }
    }

    private static string NormalizeRetractionMatch(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public void MarkOperatorGateSatisfied(
        HumanInputRequestId requestId,
        string deliverableId,
        string evidence)
    {
        lock (_humanInputRequestLock)
        {
            if (!_humanInputRequests.TryGetValue(requestId, out var request))
            {
                throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
            }

            var satisfiedGate = request.MarkOperatorGateSatisfied(deliverableId, evidence, _clock.UtcNow);
            var goal = GetGoal(request.GoalId);
            Append(
                goal,
                request.TaskId,
                ProgressKind.OperatorGateSatisfied,
                $"Operator gate satisfied: source=clarification:{request.Id.Value}; deliverable={deliverableId.Trim()}; evidence={evidence.Trim()}",
                [satisfiedGate]);
        }
    }

    public void MarkOperatorGateSatisfied(
        GoalId goalId,
        string sourceRecordId,
        string deliverableId,
        string evidence)
    {
        var goal = GetGoal(goalId);
        var normalizedSource = string.IsNullOrWhiteSpace(sourceRecordId)
            ? throw new ArgumentException("Gate source record id cannot be empty.", nameof(sourceRecordId))
            : sourceRecordId.Trim();
        var normalizedDeliverable = string.IsNullOrWhiteSpace(deliverableId)
            ? throw new ArgumentException("Gate deliverable id cannot be empty.", nameof(deliverableId))
            : deliverableId.Trim();
        var normalizedEvidence = string.IsNullOrWhiteSpace(evidence)
            ? throw new ArgumentException("Gate satisfaction evidence cannot be empty.", nameof(evidence))
            : evidence.Trim();
        var matches = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.OperatorTaskNote)
            .SelectMany(evt => (evt.OperatorGates ?? []).Select(gate => (evt, gate)))
            .Where(item =>
                string.Equals(item.gate.SourceRecordId, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.gate.DeliverableId, normalizedDeliverable, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException(
                $"Operator task-note gate '{normalizedDeliverable}' from '{normalizedSource}' was not found on goal '{goal.Id.Value[..8]}'.");
        }

        var alreadySatisfied = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.OperatorGateSatisfied)
            .SelectMany(evt => evt.OperatorGates ?? [])
            .Any(gate =>
                !gate.IsActive &&
                string.Equals(gate.SourceRecordId, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(gate.DeliverableId, normalizedDeliverable, StringComparison.OrdinalIgnoreCase));
        if (alreadySatisfied)
        {
            throw new InvalidOperationException(
                $"Operator task-note gate '{normalizedDeliverable}' from '{normalizedSource}' is already satisfied.");
        }

        var satisfiedAt = _clock.UtcNow;
        var satisfiedGate = matches[0].gate with
        {
            SatisfiedAt = satisfiedAt,
            SatisfactionEvidence = normalizedEvidence
        };
        Append(
            goal,
            matches[0].evt.TaskId,
            ProgressKind.OperatorGateSatisfied,
            $"Operator gate satisfied: source={normalizedSource}; deliverable={normalizedDeliverable}; evidence={normalizedEvidence}",
            [satisfiedGate],
            satisfiedAt);
    }

    private void RestoreTaskAfterHumanInput(Goal goal, TaskSpec task)
    {
        if (TryCompleteTaskWithPassingVerification(goal, task, "Task completed after required human input was answered."))
        {
            return;
        }

        var restoredStatus = task.LastProcess is { IsRunning: true } ||
            (task.LastDispatch is not null && task.LastProcess is null)
            ? WorkTaskStatus.Running
            : WorkTaskStatus.Assigned;
        if (task.Status == restoredStatus)
        {
            return;
        }

        task.SetStatus(restoredStatus);
        Append(goal, task.Id, ProgressKind.TaskUpdated, $"Human input resolved; restored task status to {restoredStatus}.");
    }

    public void DismissHumanInput(HumanInputRequestId requestId)
    {
        if (!_humanInputRequests.TryGetValue(requestId, out var request))
        {
            throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
        }

        if (request.IsCompleted)
        {
            throw new InvalidOperationException($"Human input request '{requestId}' has already been answered.");
        }

        if (!request.IsDismissible)
        {
            throw new InvalidOperationException($"Human input request '{requestId}' is not dismissible.");
        }

        var goal = GetGoal(request.GoalId);
        request.Dismiss(_clock.UtcNow);

        if (request.TaskId is not null)
        {
            goal.FindTask(request.TaskId).SetStatus(WorkTaskStatus.Running);
        }

        RefreshGoalStatus(goal);
        Append(goal, request.TaskId, ProgressKind.HumanInputReceived, $"Dismissed human wait {request.Id.Value[..8]}.");
    }

    public int SweepParkedGoalHumanWaits()
    {
        var resolved = 0;
        foreach (var goal in _goals.Values)
        {
            var parkResolution = ResolveParkResolution(goal);
            if (parkResolution is null)
            {
                continue;
            }

            if (goal.Status != GoalStatus.Parked)
            {
                goal.SetStatus(GoalStatus.Parked);
                Append(goal, null, ProgressKind.GoalPolicyDecision, parkResolution);
            }

            resolved += CompleteOpenHumanInputRequestsForGoal(goal.Id, parkResolution);
        }

        return resolved;
    }

    public IReadOnlyList<HumanWaitPolicyResult> SweepStaleHumanWaits(TimeSpan specClarificationStaleAfter)
    {
        var resolved = new List<HumanWaitPolicyResult>();
        foreach (var request in _humanInputRequests.Values
            .Where(request => !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList())
        {
            var age = _clock.UtcNow - request.CreatedAt;
            if (request.Kind != HumanWaitKind.SpecClarification || age < specClarificationStaleAfter)
            {
                continue;
            }

            if (request.IsAutoDefaultable && !string.IsNullOrWhiteSpace(request.SuggestedDefaultAnswer))
            {
                SubmitHumanInput(request.Id, request.SuggestedDefaultAnswer);
                resolved.Add(new HumanWaitPolicyResult(request.Id, request.GoalId, request.TaskId, request.Kind, HumanWaitPolicyResolution.Defaulted));
                continue;
            }

            if (!request.IsAnswerRequired && request.IsDismissible)
            {
                DismissHumanInput(request.Id);
                resolved.Add(new HumanWaitPolicyResult(request.Id, request.GoalId, request.TaskId, request.Kind, HumanWaitPolicyResolution.Dismissed));
            }
        }

        return resolved;
    }

    private int CompleteOpenHumanInputRequestsForGoal(GoalId goalId, string resolution)
    {
        var goal = GetGoal(goalId);
        var completed = 0;
        foreach (var request in _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList())
        {
            request.Complete(resolution, _clock.UtcNow, briefVersion: goal.AuthoritativeBrief.Version);
            completed++;
            Append(goal, request.TaskId, ProgressKind.HumanInputReceived, resolution);
        }

        RestoreTasksAfterResolvedHumanInput(goal);
        return completed;
    }

    private void RestoreTasksAfterResolvedHumanInput(Goal goal)
    {
        foreach (var task in goal.Tasks.Where(task =>
                     task.Status == WorkTaskStatus.WaitingForHuman &&
                     !_humanInputRequests.Values.Any(request =>
                         request.GoalId == goal.Id &&
                         request.TaskId == task.Id &&
                         !request.IsCompleted)))
        {
            RestoreTaskAfterHumanInput(goal, task);
        }
    }

    private static bool HasHumanInputResolvedAfterLatestParkDecision(Goal goal)
    {
        DateTimeOffset? latestParkDecisionAt = null;
        DateTimeOffset? latestHumanInputReceivedAt = null;
        foreach (var evt in goal.Timeline)
        {
            if (evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
            {
                latestParkDecisionAt = evt.OccurredAt;
            }
            else if (evt.Kind == ProgressKind.HumanInputReceived)
            {
                if (evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                latestHumanInputReceivedAt = evt.OccurredAt;
            }
        }

        return latestParkDecisionAt is not null && latestHumanInputReceivedAt > latestParkDecisionAt;
    }

    private string? ResolveParkResolution(Goal goal)
    {
        if (goal.Status == GoalStatus.Parked)
        {
            return goal.Timeline
                .LastOrDefault(evt =>
                    evt.Kind == ProgressKind.GoalPolicyDecision &&
                    evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
                ?.Message ?? "Goal parked.";
        }

        if (goal.Status != GoalStatus.WaitingForHuman)
        {
            return null;
        }

        return _humanInputRequests.Values
            .Where(request => request.GoalId == goal.Id && !request.IsCompleted)
            .OrderByDescending(request => request.RequestedAt)
            .Select(request => request.Question)
            .FirstOrDefault(question => question.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildGoalParkResolution(string reason) => $"Goal parked: {reason}";

    public Goal GetGoal(GoalId goalId)
    {
        return _goals.TryGetValue(goalId, out var goal)
            ? goal
            : throw new KeyNotFoundException($"Goal '{goalId}' was not found.");
    }

    public Goal? FindGoalBySourceBacklogItemId(string backlogItemId)
    {
        if (string.IsNullOrWhiteSpace(backlogItemId))
            return null;

        return _goals.Values.FirstOrDefault(goal =>
            string.Equals(goal.SourceBacklogItemId, backlogItemId, StringComparison.Ordinal));
    }

    public IReadOnlyList<ProgressEvent> GetTimeline(GoalId goalId) => GetGoal(goalId).Timeline;

    public void RecordGoalPolicyDecision(GoalId goalId, string message)
    {
        var goal = GetGoal(goalId);
        Append(goal, null, ProgressKind.GoalPolicyDecision, message);
    }

    public GoalHoldObservation ObserveGoalHold(
        GoalId goalId,
        string state,
        string blocker,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        string? stableIdentity = null) =>
        GetGoal(goalId).ObserveHold(state, blocker, observedAt, stallThreshold, stableIdentity);

    public bool ClearGoalHold(GoalId goalId) => GetGoal(goalId).ClearHold();

    public void SetGoalSourceBacklogItemId(GoalId goalId, string backlogItemId)
    {
        GetGoal(goalId).SetSourceBacklogItemId(backlogItemId);
    }

    public void SetGoalSourceBacklogItemLink(
        GoalId goalId,
        string backlogItemId,
        SourceBacklogCoverage coverage)
    {
        GetGoal(goalId).SetSourceBacklogItemLink(backlogItemId, coverage);
    }

    public void SetGoalRefinedSpec(GoalId goalId, RefinedSpec spec)
    {
        GetGoal(goalId).SetRefinedSpec(spec, _clock.UtcNow);
    }

    public RefinedSpecVersion RecordGoalRefinement(GoalId goalId, RefinedSpec spec)
    {
        return GetGoal(goalId).RecordRefinedSpec(spec, _clock.UtcNow);
    }

    public void RecordGoalClarificationRound(GoalId goalId)
    {
        GetGoal(goalId).RecordClarificationRound();
    }

    public void SetGoalDependency(GoalId dependentId, GoalId dependencyId)
    {
        if (dependentId == dependencyId)
            throw new InvalidOperationException($"Goal '{dependentId.Value[..8]}' cannot depend on itself.");

        var dependent = GetGoal(dependentId);
        GetGoal(dependencyId); // validate exists

        // Cycle detection: would adding dependent → dependency create a cycle?
        // A cycle exists if dependencyId can reach dependentId through existing edges.
        if (WouldCreateCycle(dependentId, dependencyId))
            throw new InvalidOperationException(
                $"Adding dependency {dependentId.Value[..8]} → {dependencyId.Value[..8]} would create a cycle.");

        dependent.AddDependency(dependencyId);
    }

    public void RemoveGoalDependency(GoalId dependentId, GoalId dependencyId)
    {
        var dependent = GetGoal(dependentId);
        GetGoal(dependencyId);
        if (!dependent.RemoveDependency(dependencyId))
            throw new InvalidOperationException(
                $"Goal '{dependentId.Value[..8]}' does not depend on '{dependencyId.Value[..8]}'.");
    }

    public void ClearGoalDependencies(GoalId dependentId)
    {
        var dependent = GetGoal(dependentId);
        if (dependent.ClearDependencies() == 0)
            throw new InvalidOperationException($"Goal '{dependentId.Value[..8]}' has no dependencies to clear.");
    }

    private bool WouldCreateCycle(GoalId dependentId, GoalId newDependencyId)
    {
        // DFS from newDependencyId following DependsOn edges; if we reach dependentId, it's a cycle.
        var visited = new HashSet<GoalId>();
        var stack = new Stack<GoalId>();
        stack.Push(newDependencyId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == dependentId)
                return true;
            if (!visited.Add(current))
                continue;
            if (!_goals.TryGetValue(current, out var currentGoal))
                continue;
            foreach (var dep in currentGoal.DependsOn)
                stack.Push(dep);
        }

        return false;
    }

    public IReadOnlyList<HumanInputRequest> GetPendingHumanInput(GoalId goalId)
    {
        return _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList();
    }

    public HumanInputRequestCounts GetHumanInputRequestCounts(GoalId goalId, TaskId? taskId)
    {
        var requests = _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && request.TaskId == taskId)
            .ToList();
        return new HumanInputRequestCounts(requests.Count, requests.Count(request => !request.IsCompleted));
    }
}
