namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    // The execution owner supplies a persisted admission denial for this exact host attempt.
    // Suppression is not a failed worker round and must not spend or manufacture a retry.
    public void DiscardUnstartedDispatch(GoalId goalId, TaskId taskId, TaskProcessRecord process, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        if (task.LastProcess is not { } current || current.ProcessId != process.ProcessId ||
            current.StartedAt != process.StartedAt || current.ProcessIdentityStartedAt != process.ProcessIdentityStartedAt ||
            process.CompletedAt is null)
            throw new InvalidOperationException("Unstarted dispatch suppression requires the current completed host identity.");
        task.RecordProcess(process);
        if (task.Status == WorkTaskStatus.Running)
        {
            task.ReleaseUnstartedRetryAdmission(task.LastDispatch!.DispatchedAt, _clock.UtcNow);
            task.ClearLastProcess();
            task.ClearLastDispatch();
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
            RefreshGoalStatus(goal);
        }
        Append(goal, taskId, ProgressKind.TaskNote, message);
    }

    public void DiscardUnstartedPreparedDispatch(GoalId goalId, TaskId taskId, DateTimeOffset dispatchedAt, string message)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        if (task.LastProcess is not null || task.LastDispatch?.DispatchedAt != dispatchedAt || task.Status != WorkTaskStatus.Running)
            throw new InvalidOperationException("Preparation suppression requires the current unstarted running attempt.");
        task.ReleaseUnstartedRetryAdmission(dispatchedAt, _clock.UtcNow);
        task.ClearLastDispatch();
        task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned);
        RefreshGoalStatus(goal);
        Append(goal, taskId, ProgressKind.TaskNote, message);
    }
}
