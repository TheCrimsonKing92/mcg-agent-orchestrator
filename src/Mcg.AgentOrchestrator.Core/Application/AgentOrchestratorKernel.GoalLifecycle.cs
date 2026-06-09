namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public Goal CreateGoal(string objective, IReadOnlyList<TaskSpec>? tasks = null)
    {
        var goal = new Goal(GoalId.New(), objective, tasks ?? CreateDefaultSoftwareDevelopmentTasks());
        _goals.Add(goal.Id, goal);
        Append(goal, null, ProgressKind.GoalCreated, $"Goal created: {goal.Objective}");
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
            Append(goal, task.Id, ProgressKind.TaskDelegated, $"Delegated '{task.Description}' to {agent.Name}.");
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
        Append(goal, task.Id, ProgressKind.TaskAdded, $"Added {requiredRole} task: {task.Description}");

        if (availableAgents is not null)
        {
            var agent = availableAgents.FirstOrDefault(candidate =>
                candidate.Status == AgentStatus.Available && candidate.Role == requiredRole);

            if (agent is not null)
            {
                task.AssignTo(agent.Id);
                goal.SetStatus(GoalStatus.Active);
                Append(goal, task.Id, ProgressKind.TaskDelegated, $"Delegated '{task.Description}' to {agent.Name}.");
            }
        }

        return task;
    }

    public TaskSpec SetTaskVerificationPlan(GoalId goalId, TaskId taskId, string verificationPlan)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetVerificationPlan(verificationPlan);
        Append(goal, taskId, ProgressKind.TaskVerificationPlanUpdated, $"Verification plan updated: {task.VerificationPlan}");
        return task;
    }

    public TaskSpec RetryTask(GoalId goalId, TaskId taskId, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);

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
        task.ClearSubscriptionRetryAfter();
        task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
        Append(goal, taskId, ProgressKind.TaskRetried, string.IsNullOrWhiteSpace(message) ? "Retry requested." : message.Trim());
        RefreshGoalStatus(goal);
        return task;
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

    public IReadOnlyList<HumanInputRequest> GetPendingHumanInput(GoalId goalId)
    {
        return _humanInputRequests.Values
            .Where(request => request.GoalId == goalId && !request.IsCompleted)
            .OrderBy(request => request.RequestedAt)
            .ToList();
    }
}
