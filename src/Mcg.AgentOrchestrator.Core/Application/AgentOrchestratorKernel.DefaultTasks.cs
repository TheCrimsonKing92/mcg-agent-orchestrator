namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public static IReadOnlyList<TaskSpec> CreateDefaultSoftwareDevelopmentTasks()
    {
        return
        [
            new(TaskId.New(), "Research constraints, APIs, and integration risks", AgentRole.Researcher, "Verify each material research claim cites repository-local files, APIs, tests, commands, or primary external sources; separate confirmed facts from inferences and record when no research is needed and why."),
            new(TaskId.New(), "Clarify goal and decompose the SDLC plan", AgentRole.Planner, "Verify the plan maps every acceptance criterion and names likely files/modules, ownership/lifecycle, edge contracts, sequencing risks, falsifiable proof, and concrete stop conditions."),
            new(TaskId.New(), "Implement the requested software changes", AgentRole.Developer, "Run the relevant build or focused test command, or record implementation evidence with changed files and the behavior enabled when no automated command applies."),
            new(TaskId.New(), "Verify behavior with automated and manual checks", AgentRole.Tester, "Run or attempt exact automated tests or manual smoke checks, including negative or edge coverage when practical; record command, exit code, output summary, concrete pass/fail evidence, and exact failure text when a check cannot run."),
            new(TaskId.New(), "Review results, risks, and remaining work", AgentRole.Reviewer, "Review implementation output, verification history, and changed behavior in code-review form with findings first, file/evidence references, test gaps, residual risk, and acceptance recommendation.")
        ];
    }

    private void Append(Goal goal, TaskId? taskId, ProgressKind kind, string message)
    {
        var progressEvent = new ProgressEvent(goal.Id, taskId, kind, message, _clock.UtcNow);
        goal.Append(progressEvent);
        _eventWriter.AppendTimelineEvent(progressEvent);
    }

    public bool RecordTaskRequeueSkipped(
        GoalId goalId,
        TaskId taskId,
        string dispatchId,
        string blockingEntity,
        string? terminalState,
        string reason,
        string? detail = null)
    {
        var goal = GetGoal(goalId);
        var payload = new TaskRequeueSkippedPayload(
            taskId.Value,
            goalId.Value,
            dispatchId,
            blockingEntity,
            terminalState,
            reason,
            detail);
        if (goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.TaskRequeueSkipped &&
                evt.RequeueSkipped?.DispatchId == dispatchId))
        {
            return false;
        }

        var message = terminalState is null
            ? $"Skipped auto-requeue: {blockingEntity} state is unreadable ({detail ?? reason})"
            : $"Skipped auto-requeue: {blockingEntity} is {terminalState}";
        var progressEvent = new ProgressEvent(
            goalId,
            taskId,
            ProgressKind.TaskRequeueSkipped,
            message,
            _clock.UtcNow,
            payload);
        goal.Append(progressEvent);
        _eventWriter.AppendTimelineEvent(progressEvent);
        return true;
    }

    public void ConcludeInterruptedDispatchRecovery(
        GoalId goalId,
        TaskId taskId,
        GoalStatus? authoritativeGoalStatus,
        WorkTaskStatus? authoritativeTaskStatus)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetInterruptedDispatchRecovery(null);

        // A skipped automatic recovery must not remain in the locally prepared state with no
        // process. Preserve the live store status when it was readable; otherwise Cancelled is the
        // safe, explicit disposition and manual RetryTask remains the operator override.
        task.SetStatus(authoritativeTaskStatus ?? WorkTaskStatus.Cancelled);

        if (authoritativeGoalStatus is { } goalStatus && goalStatus != GoalStatus.Active)
        {
            goal.SetStatus(goalStatus);
            return;
        }

        RefreshGoalStatus(goal);
    }
}
