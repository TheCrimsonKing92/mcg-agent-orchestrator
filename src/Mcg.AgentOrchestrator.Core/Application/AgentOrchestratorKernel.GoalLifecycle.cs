namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public Goal CreateGoal(string objective, IReadOnlyList<TaskSpec>? tasks = null)
    {
        var goal = new Goal(GoalId.New(), objective, tasks ?? CreateDefaultSoftwareDevelopmentTasks());
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, "Goal created.");
        _eventWriter.AppendGoalCreated(goal.Id, objective);
        return goal;
    }

    public Goal CreateGoal(GoalId id, string objective, IReadOnlyList<TaskSpec>? tasks = null)
    {
        var goal = new Goal(id, objective, tasks ?? CreateDefaultSoftwareDevelopmentTasks());
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, "Goal created.");
        _eventWriter.AppendGoalCreated(goal.Id, objective);
        return goal;
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

    public TaskSpec AddTask(GoalId goalId, AgentRole requiredRole, string description, IReadOnlyList<AgentDefinition>? availableAgents = null, string? verificationPlan = null)
    {
        var goal = GetGoal(goalId);
        var task = new TaskSpec(TaskId.New(), description, requiredRole, verificationPlan);
        goal.AddTask(task);
        Append(goal, task.Id, ProgressKind.TaskAdded, $"Added {requiredRole} task.");

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

        Append(goal, taskId, ProgressKind.TaskNote, noteMessage);
        return task;
    }

    public TaskSpec RetryTask(GoalId goalId, TaskId taskId, string message, bool invalidateDownstream = true)
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

        ResetTaskForRetry(task);
        Append(goal, taskId, ProgressKind.TaskRetried, retryMessage);
        if (invalidateDownstream)
        {
            InvalidateDownstreamTasks(goal, task);
        }
        ReopenTerminalGoalWithNonTerminalTasks(goal, $"Retry reopened goal because task {task.Id.Value[..8]} is dispatchable.");
        RefreshGoalStatus(goal);
        return task;
    }

    public bool NormalizeGoalLifecycleState(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        return ReopenTerminalGoalWithNonTerminalTasks(goal, reason);
    }

    public bool NormalizePrematureCompletedGoalToVerified(GoalId goalId, string reason)
    {
        var goal = GetGoal(goalId);
        if (goal.Status != GoalStatus.Completed)
        {
            return false;
        }

        if (!goal.Tasks.All(task => BuildTaskVerificationGate(task).GateStatus == VerificationGateStatus.Passed))
        {
            return false;
        }

        goal.SetStatus(GoalStatus.Verified);
        Append(goal, null, ProgressKind.GoalPolicyDecision, reason);
        return true;
    }

    public TaskSpec RequeueInterruptedDispatch(GoalId goalId, TaskId taskId, string message)
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
        return task.CriterionRetryCount;
    }

    public void RecordAcceptanceFailure(GoalId goalId, IReadOnlyList<string> failedChecks)
    {
        var goal = GetGoal(goalId);
        goal.RecordAcceptanceFailure(failedChecks, _clock.UtcNow);
    }

    public void ClearAcceptanceFailure(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        goal.ClearAcceptanceFailure();
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

        if (goal.Status != GoalStatus.Verified)
        {
            throw new InvalidOperationException($"Goal '{goalId}' is {goal.Status}; only Verified goals can be completed.");
        }

        goal.SetStatus(GoalStatus.Completed);
        Append(goal, null, ProgressKind.GoalPolicyDecision, completeReason);
        return goal;
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

        Append(goal, taskId, kind, message);

        RefreshGoalStatus(goal);
    }

    private static void ResetTaskForRetry(TaskSpec task)
    {
        task.ClearLatestVerification();
        task.ClearLastExecution();
        task.ClearLastDispatch();
        task.ClearLastProcess();
        task.ClearSubscriptionRetryAfter();
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

    private void InvalidateDownstreamTasks(Goal goal, TaskSpec retriedTask)
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

            ResetTaskForRetry(downstream);
            Append(
                goal,
                downstream.Id,
                ProgressKind.TaskRetried,
                $"Invalidated {downstream.RequiredRole} task because upstream {retriedTask.RequiredRole} task {retriedTask.Id.Value[..8]} was retried.");
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
        AgentRole.Planner => 0,
        AgentRole.Ideation => 1,
        AgentRole.Researcher => 2,
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
        var goal = GetGoal(goalId);
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
            resumeCommand ?? HumanInputRequest.BuildDefaultResumeCommand(id));
        _humanInputRequests.Add(request.Id, request);

        if (taskId is not null)
        {
            goal.FindTask(taskId).SetStatus(WorkTaskStatus.WaitingForHuman);
        }

        goal.SetStatus(GoalStatus.WaitingForHuman);
        Append(goal, taskId, ProgressKind.HumanInputRequested, question);
        return request;
    }

    public void SubmitHumanInput(HumanInputRequestId requestId, string answer)
    {
        if (!_humanInputRequests.TryGetValue(requestId, out var request))
        {
            throw new KeyNotFoundException($"Human input request '{requestId}' was not found.");
        }

        if (request.IsCompleted)
        {
            throw new InvalidOperationException($"Human input request '{requestId}' has already been answered.");
        }

        var goal = GetGoal(request.GoalId);
        request.Complete(answer, _clock.UtcNow);

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

        Append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer);
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
            : task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned;
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
        var completed = 0;
        foreach (var request in _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList())
        {
            request.Complete(resolution, _clock.UtcNow);
            completed++;
            Append(GetGoal(goalId), request.TaskId, ProgressKind.HumanInputReceived, resolution);
        }

        return completed;
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

    public void SetGoalSourceBacklogItemId(GoalId goalId, string backlogItemId)
    {
        GetGoal(goalId).SetSourceBacklogItemId(backlogItemId);
    }

    public void SetGoalRefinedSpec(GoalId goalId, RefinedSpec spec)
    {
        GetGoal(goalId).SetRefinedSpec(spec);
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
}
