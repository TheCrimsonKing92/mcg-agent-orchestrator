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

    private void Append(
        Goal goal,
        TaskId? taskId,
        ProgressKind kind,
        string message,
        IReadOnlyList<OperatorGateRecord>? operatorGates = null,
        DateTimeOffset? occurredAt = null)
    {
        var progressEvent = new ProgressEvent(
            goal.Id,
            taskId,
            kind,
            message,
            occurredAt ?? _clock.UtcNow,
            OperatorGates: operatorGates);
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

    public bool RecordOperatorIntentApplied(
        GoalId goalId,
        string intentId,
        string verb,
        string? taskId,
        string actor,
        string channel,
        string? authenticationAssurance,
        string message,
        OperatorActorKind actorKind = OperatorActorKind.Human,
        string? decisionId = null,
        string? outcome = null)
    {
        var goal = GetGoal(goalId);
        var payload = new OperatorIntentAppliedPayload(
            intentId,
            verb,
            taskId,
            actor,
            channel,
            authenticationAssurance,
            actorKind,
            decisionId,
            outcome);
        if (!string.IsNullOrEmpty(intentId) &&
            goal.Timeline.Any(evt => evt.OperatorIntentApplied?.IntentId == intentId))
        {
            return false;
        }

        var progressEvent = new ProgressEvent(
            goalId,
            null,
            ProgressKind.GoalPolicyDecision,
            message,
            _clock.UtcNow,
            OperatorIntentApplied: payload);
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
        if (authoritativeGoalStatus is null || authoritativeTaskStatus is null)
        {
            throw new InvalidOperationException(
                "Interrupted dispatch recovery can only be concluded from readable authoritative state.");
        }

        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        task.SetInterruptedDispatchRecovery(null);

        task.SetStatus(authoritativeTaskStatus.Value);

        if (authoritativeGoalStatus.Value != GoalStatus.Active)
        {
            goal.SetStatus(authoritativeGoalStatus.Value);
            return;
        }

        RefreshGoalStatus(goal);
    }
}
