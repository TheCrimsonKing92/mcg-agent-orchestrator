namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public Goal CreateGoal(string objective, IReadOnlyList<TaskSpec>? tasks = null)
    {
        var goal = new Goal(GoalId.New(), objective, tasks ?? CreateDefaultSoftwareDevelopmentTasks());
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, "Goal created.");
        return goal;
    }

    public Goal CreateGoal(GoalId id, string objective, IReadOnlyList<TaskSpec>? tasks = null)
    {
        var goal = new Goal(id, objective, tasks ?? CreateDefaultSoftwareDevelopmentTasks());
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, "Goal created.");
        return goal;
    }

    public DelegationPlan ActivateGoal(GoalId goalId, IReadOnlyList<AgentDefinition> availableAgents)
    {
        var goal = GetGoal(goalId);
        var assignments = new List<TaskAssignment>();

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Pending))
        {
            var agent = availableAgents.FirstOrDefault(candidate =>
                candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole);

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
            var agent = availableAgents.FirstOrDefault(candidate =>
                candidate.Status == AgentStatus.Available && candidate.Role == requiredRole);

            if (agent is not null)
            {
                task.AssignTo(agent.Id);
                goal.SetStatus(GoalStatus.Active);
                Append(goal, task.Id, ProgressKind.TaskDelegated, $"Delegated {task.RequiredRole} task to {agent.Name}.");
            }
        }

        return task;
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

    public TaskSpec RetryTask(GoalId goalId, TaskId taskId, string message)
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

        task.ClearLatestVerification();
        task.ClearLastExecution();
        task.ClearLastDispatch();
        task.ClearLastProcess();
        task.ClearSubscriptionRetryAfter();
        task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
        Append(goal, taskId, ProgressKind.TaskRetried, retryMessage);
        RefreshGoalStatus(goal);
        return task;
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

    public HumanInputRequest RequestHumanInput(GoalId goalId, TaskId? taskId, string question)
    {
        var goal = GetGoal(goalId);
        var request = new HumanInputRequest(HumanInputRequestId.New(), goal.Id, taskId, question, _clock.UtcNow);
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
            if (!TryCompleteTaskWithPassingVerification(goal, task, "Task completed after required human input was answered."))
            {
                task.SetStatus(WorkTaskStatus.Running);
            }
        }

        RefreshGoalStatus(goal);

        Append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer);
    }

    public Goal GetGoal(GoalId goalId)
    {
        return _goals.TryGetValue(goalId, out var goal)
            ? goal
            : throw new KeyNotFoundException($"Goal '{goalId}' was not found.");
    }

    public IReadOnlyList<ProgressEvent> GetTimeline(GoalId goalId) => GetGoal(goalId).Timeline;

    public void RecordGoalPolicyDecision(GoalId goalId, string message)
    {
        var goal = GetGoal(goalId);
        Append(goal, null, ProgressKind.GoalPolicyDecision, message);
    }

    public IReadOnlyList<HumanInputRequest> GetPendingHumanInput(GoalId goalId)
    {
        return _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList();
    }
}
