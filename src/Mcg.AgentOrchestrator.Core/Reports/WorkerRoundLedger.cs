namespace Mcg.AgentOrchestrator.Core;

public enum WorkerRoundStopCause
{
    Completed,
    Failed,
    Cancelled,
    Superseded,
    Open,
    Unknown
}

public sealed record WorkerRoundRecord(
    string GoalId,
    string TaskId,
    AgentRole Role,
    string WorkerName,
    int RoundIndex,
    DateTimeOffset DispatchedAt,
    DateTimeOffset? EndedAt,
    WorkerRoundStopCause StopCause,
    string? ProviderName,
    string? ModelName,
    string? DispatchLane,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens)
{
    public bool UsageReported => InputTokens.HasValue && CachedInputTokens.HasValue && OutputTokens.HasValue;
}

public static class WorkerRoundLedger
{
    public static IReadOnlyList<WorkerRoundRecord> FromGoals(IEnumerable<Goal> goals)
    {
        ArgumentNullException.ThrowIfNull(goals);
        return goals.OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .SelectMany(FromGoal).ToArray();
    }

    public static IReadOnlyList<WorkerRoundRecord> FromGoal(Goal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var records = new List<WorkerRoundRecord>();
        foreach (var task in goal.Tasks)
        {
            var dispatches = task.DispatchHistory.OrderBy(dispatch => dispatch.DispatchedAt).ToArray();
            var endings = goal.Timeline
                .Where(e => e.TaskId == task.Id && StopCause(e.Kind) is not null)
                .OrderBy(e => e.OccurredAt).ToArray();
            for (var index = 0; index < dispatches.Length; index++)
            {
                var dispatch = dispatches[index];
                var nextAt = index + 1 < dispatches.Length
                    ? dispatches[index + 1].DispatchedAt : (DateTimeOffset?)null;
                var ending = endings.FirstOrDefault(e => e.OccurredAt >= dispatch.DispatchedAt &&
                    (nextAt is null || e.OccurredAt < nextAt));
                var cause = ending is not null ? StopCause(ending.Kind)!.Value
                    : nextAt is null && task.Status is WorkTaskStatus.Running or WorkTaskStatus.Assigned
                        ? WorkerRoundStopCause.Open : WorkerRoundStopCause.Unknown;
                var usage = dispatch.ContextPackageReceipt;
                records.Add(new WorkerRoundRecord(goal.Id.Value, task.Id.Value, task.RequiredRole,
                    dispatch.WorkerName, index + 1, dispatch.DispatchedAt, ending?.OccurredAt,
                    cause, dispatch.ProviderName, dispatch.ModelName, dispatch.DispatchLane,
                    Reported(usage?.InputTokens), Reported(usage?.CachedInputTokens),
                    Reported(usage?.OutputTokens)));
            }
        }
        return records;
    }

    private static long? Reported(ProviderUsageValue? value) =>
        value?.State == ProviderUsageState.Reported ? value.Value : null;

    private static WorkerRoundStopCause? StopCause(ProgressKind kind) => kind switch
    {
        ProgressKind.TaskCompleted => WorkerRoundStopCause.Completed,
        ProgressKind.TaskFailed => WorkerRoundStopCause.Failed,
        ProgressKind.TaskCancelled => WorkerRoundStopCause.Cancelled,
        ProgressKind.TaskRetried => WorkerRoundStopCause.Superseded,
        _ => null
    };
}
