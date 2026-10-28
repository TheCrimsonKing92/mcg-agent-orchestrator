namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderUsageHoldRow(ProviderBudgetExhaustionHold Hold, ProviderBudgetHoldSchedule? Schedule);

/// <summary>Only the canonical inputs consumed by the existing binding classifier and scheduler.</summary>
public sealed record ProviderUsageTaskEvidence(string TaskId, AgentRole RequiredRole, WorkTaskStatus Status, string? AssignedAgentId,
    TaskDispatchSnapshot? LastDispatch, IReadOnlyList<TaskDispatchSnapshot> DispatchHistory,
    TaskVerificationSnapshot? LastVerification, IReadOnlyList<TaskVerificationSnapshot> VerificationHistory,
    DateTimeOffset? LatestProviderBudgetRecoveryAt);

public sealed record ProviderUsageGoalEvidence(string GoalId, GoalStatus Status,
    IReadOnlyList<ProviderUsageTaskEvidence> Tasks, IReadOnlyList<ProgressEvent> Timeline);

/// <summary>Read-only operator projection of held dispatched bindings and their probe schedules.</summary>
public static class ProviderUsageHoldView
{
    public static IReadOnlyList<ProviderUsageHoldRow> ReadEvidence(
        IReadOnlyList<ProviderUsageGoalEvidence> evidence, DateTimeOffset now,
        IReadOnlyCollection<Goal>? restoredOwners = null)
    {
        // Sparse, invocation-local policy adapters. Never deserialize historical GoalSnapshot/task
        // graphs or restore a kernel. TaskSpec owns legacy last/history merge and verification rules.
        var goals = evidence.Select(row =>
        {
            if (restoredOwners?.FirstOrDefault(goal => goal.Id.Value == row.GoalId) is { } restored)
                return restored;
            var tasks = row.Tasks.Select(task => TaskSpec.FromSnapshot(new TaskSnapshot(
                task.TaskId, "Provider policy input", task.RequiredRole, task.Status, task.AssignedAgentId,
                null, task.LastVerification, task.VerificationHistory, task.LastDispatch, null,
                DispatchHistory: task.DispatchHistory, LatestProviderBudgetRecoveryAt: task.LatestProviderBudgetRecoveryAt)))
                .ToArray();
            var goal = tasks.Length == 0
                ? Goal.CreateMetadataOnlyTerminal(new TerminalGoalMetadata(new GoalId(row.GoalId),
                    GoalStatus.Completed, "Provider policy input"))
                : new Goal(new GoalId(row.GoalId), "Provider policy input", tasks);
            goal.SetStatus(row.Status);
            foreach (var evt in row.Timeline.OrderBy(evt => evt.OccurredAt)) goal.Append(evt);
            return goal;
        }).ToArray();
        return Read(goals, now);
    }

    public static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> goals, DateTimeOffset now) =>
        Read(goals, goals, now);

    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> holdScope,
        IReadOnlyCollection<Goal> scheduleScope, DateTimeOffset now)
    {
        var bindings = holdScope.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory)
            .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName,
                Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
        var holds = new Dictionary<string, ProviderBudgetExhaustionHold>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
            if (DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(holdScope,
                    binding.Provider, binding.Credential, out var hold))
                holds.TryAdd(hold.BindingKey, hold);
        return holds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            ProviderBudgetHoldSchedule? schedule;
            try { schedule = ProviderBudgetHoldSchedules.Evaluate(scheduleScope, pair.Value, now); }
            catch (InvalidOperationException) { schedule = null; }
            return new ProviderUsageHoldRow(pair.Value, schedule);
        }).ToArray();
    }

    public static string FormatStatusLine(ProviderUsageHoldRow row)
    {
        var source = $"source={row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}";
        return row.Schedule is { } schedule
            ? $"  {row.Hold.BindingKey} | attempt={schedule.Attempt} | next_probe={ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt)} | reset_source={schedule.ResetSource} | last_probe={schedule.LastProbeOutcome} | {source}"
            : $"  {row.Hold.BindingKey} | schedule=unavailable | {source}";
    }
}
