namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public GoalMonitor BuildMonitor(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var pendingHumanInput = GetPendingHumanInput(goalId);
        var attention = new List<TaskAttentionItem>();

        foreach (var request in pendingHumanInput)
        {
            attention.Add(new TaskAttentionItem(
                TaskAttentionKind.PendingHumanInput,
                request.TaskId,
                request.Question));
        }

        foreach (var task in goal.Tasks)
        {
            if (task.Status == WorkTaskStatus.Failed)
            {
                attention.Add(new TaskAttentionItem(
                    TaskAttentionKind.FailedTask,
                    task.Id,
                    $"{task.RequiredRole}: {task.Description}"));
            }

            if (task.LastVerification is { Succeeded: false })
            {
                attention.Add(new TaskAttentionItem(
                    TaskAttentionKind.FailedVerification,
                    task.Id,
                    $"exit={task.LastVerification.ExitCode}: {task.LastVerification.Command}"));
            }

            if (task.Status == WorkTaskStatus.Running && task.LastDispatch is not null)
            {
                attention.Add(new TaskAttentionItem(
                    TaskAttentionKind.RunningDispatch,
                    task.Id,
                    $"{task.LastDispatch.WorkerName}: {task.LastDispatch.Command}"));
            }

            if (task.LastProcess is { IsRunning: true })
            {
                attention.Add(new TaskAttentionItem(
                    TaskAttentionKind.RunningDispatch,
                    task.Id,
                    $"pid={task.LastProcess.ProcessId}: {task.LastProcess.Command}"));
            }

            if (task.Status == WorkTaskStatus.Completed && task.LastVerification is null)
            {
                attention.Add(new TaskAttentionItem(
                    TaskAttentionKind.MissingVerification,
                    task.Id,
                    $"{task.RequiredRole}: {task.Description}"));
            }
        }

        var counts = goal.Tasks
            .GroupBy(task => task.Status)
            .Select(group => new TaskStatusCount(group.Key, group.Count()))
            .OrderBy(count => count.Status)
            .ToList();

        return new GoalMonitor(
            goal.Id,
            goal.Objective,
            goal.Status,
            goal.Tasks.Count,
            counts,
            pendingHumanInput.Count,
            attention,
            goal.Timeline.OrderBy(evt => evt.OccurredAt).LastOrDefault()?.OccurredAt);
    }

    public GoalNextActions BuildNextActions(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var items = new List<NextActionItem>();

        if (IsTerminalGoalStatus(goal.Status))
        {
            items.Add(new NextActionItem(
                NextActionKind.MonitorGoal,
                null,
                null,
                $"Goal is {goal.Status}; no further action is required."));
            return new GoalNextActions(goal.Id, goal.Objective, goal.Status, items);
        }

        foreach (var request in GetPendingHumanInput(goalId).OrderBy(request => request.RequestedAt))
        {
            items.Add(new NextActionItem(
                NextActionKind.AnswerHumanInput,
                request.TaskId,
                request.Id,
                $"{request.Kind} wait age={FormatAge(_clock.UtcNow - request.CreatedAt)}: {request.Question}. Resume: {request.ResumeCommand}",
                request.ResumeCommand));
        }

        foreach (var task in goal.Tasks)
        {
            if (task.Status == WorkTaskStatus.Failed)
            {
                var message = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery)
                    ? BuildDirtyDispatchRecoveryMessage(recovery)
                    : $"{task.RequiredRole}: {task.Description}";
                items.Add(new NextActionItem(
                    NextActionKind.InspectFailedTask,
                    task.Id,
                    null,
                    message));
            }

            if (task.LastVerification is { Succeeded: false })
            {
                var message = DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery)
                    ? BuildDirtyDispatchRecoveryMessage(recovery)
                    : $"Last verification failed with exit {task.LastVerification.ExitCode}: {task.LastVerification.Command}";
                items.Add(new NextActionItem(
                    NextActionKind.FixFailedVerification,
                    task.Id,
                    null,
                    message));
            }
        }

        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is { IsRunning: true })
            {
                items.Add(new NextActionItem(
                    NextActionKind.RefreshRunningProcess,
                    task.Id,
                    null,
                    $"Refresh process {task.LastProcess.ProcessId}: {task.LastProcess.Command}"));
            }
            else if (task.Status == WorkTaskStatus.Running && task.LastDispatch is not null)
            {
                items.Add(new NextActionItem(
                    NextActionKind.ExecuteRecordedDispatch,
                    task.Id,
                    null,
                    $"{task.LastDispatch.WorkerName}: {task.LastDispatch.Command}"));
            }
        }

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Completed && task.LastVerification is null))
        {
            items.Add(new NextActionItem(
                NextActionKind.VerifyCompletedTask,
                task.Id,
                null,
                $"{task.RequiredRole}: {task.Description}"));
        }

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
        {
            items.Add(new NextActionItem(
                NextActionKind.RunAssignedTask,
                task.Id,
                null,
                $"{task.RequiredRole}: {task.Description}"));
        }

        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Pending))
        {
            items.Add(new NextActionItem(
                NextActionKind.DelegatePendingTask,
                task.Id,
                null,
                $"{task.RequiredRole}: {task.Description}"));
        }

        if (items.Count == 0)
        {
            items.Add(new NextActionItem(
                NextActionKind.MonitorGoal,
                null,
                null,
                "No immediate action is required; monitor the goal for new progress."));
        }

        return new GoalNextActions(goal.Id, goal.Objective, goal.Status, items);
    }

    private static string BuildDirtyDispatchRecoveryMessage(DirtyDispatchRecovery recovery)
    {
        var changed = string.Join(", ", recovery.ChangedFiles);
        var evidence = recovery.HasUsefulVerification
            ? string.Join("; ", recovery.VerificationEvidence)
            : "no verification evidence found; rerun focused tests before committing";
        return $"{recovery.Label} dispatch recovery needed: changed files [{changed}]; verification evidence: {evidence}.";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays}d";
        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours}h";
        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes}m";
        return $"{Math.Max(0, (int)age.TotalSeconds)}s";
    }
}
