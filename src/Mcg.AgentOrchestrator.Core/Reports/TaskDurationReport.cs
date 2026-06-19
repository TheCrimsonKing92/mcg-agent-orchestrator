namespace Mcg.AgentOrchestrator.Core;

public sealed record TaskDurationObservation(
    string GoalId,
    string TaskId,
    AgentRole Role,
    TaskComplexity Complexity,
    string? ProviderName,
    string? ModelName,
    TimeSpan? LegitimateRuntime,
    TimeSpan FailureInterventionOverhead,
    int AttemptCount,
    int FailedAttemptCount);

public sealed record TaskDurationStatsRecord(
    AgentRole Role,
    TaskComplexity Complexity,
    string? ProviderName,
    string? ModelName,
    int TaskCount,
    int AttemptCount,
    int FailedAttemptCount,
    TimeSpan? MedianLegitimateRuntime,
    TimeSpan? P90LegitimateRuntime,
    TimeSpan? MedianFailureInterventionOverhead,
    double FailureRate)
{
    public string Scope => string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(ModelName)
        ? $"{Role}/{Complexity}"
        : $"{Role}/{Complexity} {ProviderName}/{ModelName}";
}

public static class TaskDurationReport
{
    public static IReadOnlyList<TaskDurationStatsRecord> BuildByRoleAndComplexity(IEnumerable<Goal> goals) =>
        Build(CollectObservations(goals), includeModel: false);

    public static IReadOnlyList<TaskDurationStatsRecord> BuildByRoleComplexityAndModel(IEnumerable<Goal> goals) =>
        Build(CollectObservations(goals), includeModel: true);

    public static IReadOnlyList<TaskDurationObservation> CollectObservations(IEnumerable<Goal> goals)
    {
        var observations = new List<TaskDurationObservation>();
        foreach (var goal in goals)
        {
            foreach (var task in goal.Tasks)
            {
                var observation = TryBuildObservation(goal, task);
                if (observation is not null)
                {
                    observations.Add(observation);
                }
            }
        }

        return observations;
    }

    public static TaskDurationStatsRecord? FindEstimate(
        IEnumerable<TaskDurationStatsRecord> stats,
        AgentRole role,
        TaskComplexity complexity)
    {
        return stats
            .Where(record => record.Role == role && record.Complexity == complexity)
            .Where(record => record.MedianLegitimateRuntime is not null)
            .OrderByDescending(record => record.TaskCount)
            .ThenBy(record => record.Scope, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<TaskDurationStatsRecord> Build(
        IEnumerable<TaskDurationObservation> observations,
        bool includeModel)
    {
        return observations
            .GroupBy(observation => (
                observation.Role,
                observation.Complexity,
                ProviderName: includeModel ? observation.ProviderName : null,
                ModelName: includeModel ? observation.ModelName : null))
            .OrderBy(group => group.Key.Role)
            .ThenBy(group => group.Key.Complexity)
            .ThenBy(group => group.Key.ProviderName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var legitimate = items
                    .Select(item => item.LegitimateRuntime)
                    .Where(duration => duration is not null)
                    .Select(duration => duration!.Value)
                    .OrderBy(duration => duration)
                    .ToList();
                var overhead = items
                    .Select(item => item.FailureInterventionOverhead)
                    .Where(duration => duration > TimeSpan.Zero)
                    .OrderBy(duration => duration)
                    .ToList();
                var attempts = items.Sum(item => item.AttemptCount);
                var failedAttempts = items.Sum(item => item.FailedAttemptCount);

                return new TaskDurationStatsRecord(
                    group.Key.Role,
                    group.Key.Complexity,
                    group.Key.ProviderName,
                    group.Key.ModelName,
                    items.Count,
                    attempts,
                    failedAttempts,
                    Percentile(legitimate, 0.5),
                    Percentile(legitimate, 0.9),
                    Percentile(overhead, 0.5),
                    attempts > 0 ? (double)failedAttempts / attempts : 0.0);
            })
            .ToList();
    }

    private static TaskDurationObservation? TryBuildObservation(Goal goal, TaskSpec task)
    {
        var dispatchTimes = goal.Timeline
            .Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .Select(evt => evt.OccurredAt)
            .OrderBy(timestamp => timestamp)
            .ToList();

        if (task.LastDispatch is not null &&
            !dispatchTimes.Any(timestamp => timestamp == task.LastDispatch.DispatchedAt))
        {
            dispatchTimes.Add(task.LastDispatch.DispatchedAt);
            dispatchTimes.Sort();
        }

        if (dispatchTimes.Count == 0)
        {
            return null;
        }

        var verifications = task.VerificationHistory
            .OrderBy(verification => verification.CompletedAt)
            .ToList();
        var attempts = PairAttempts(dispatchTimes, verifications);
        if (attempts.Count == 0)
        {
            return null;
        }

        var successfulAttempts = attempts
            .Where(attempt => attempt.Verification?.Succeeded is true)
            .ToList();
        var legitimateRuntime = successfulAttempts.Count == 0
            ? (TimeSpan?)null
            : Sum(successfulAttempts.Select(attempt => PositiveDuration(attempt.End, ResolveAttemptStart(task, attempt, dispatchTimes))));

        var overhead = TimeSpan.Zero;
        var failedAttemptCount = 0;
        foreach (var attempt in attempts)
        {
            if (attempt.Verification?.Succeeded is true)
            {
                continue;
            }

            failedAttemptCount++;
            overhead += PositiveDuration(attempt.End, ResolveAttemptStart(task, attempt, dispatchTimes));
            if (attempt.Verification is not null && attempt.NextDispatchAt is not null)
            {
                overhead += PositiveDuration(attempt.NextDispatchAt.Value, attempt.Verification.CompletedAt);
            }
        }

        if (task.LastProcess is { WasCancelled: true, CompletedAt: { } cancelledAt } process &&
            !attempts.Any(attempt => attempt.End == cancelledAt))
        {
            failedAttemptCount++;
            overhead += PositiveDuration(cancelledAt, process.StartedAt);
        }

        var complexity = task.LastDispatch?.TaskComplexity is { } recorded and not TaskComplexity.Auto
            ? recorded
            : TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);

        return new TaskDurationObservation(
            goal.Id.Value,
            task.Id.Value,
            task.RequiredRole,
            complexity,
            task.LastDispatch?.ProviderName,
            task.LastDispatch?.ModelName,
            legitimateRuntime,
            overhead,
            attempts.Count,
            failedAttemptCount);
    }

    private static List<AttemptTiming> PairAttempts(
        IReadOnlyList<DateTimeOffset> dispatchTimes,
        IReadOnlyList<TaskVerificationRecord> verifications)
    {
        var attempts = new List<AttemptTiming>();
        for (var index = 0; index < dispatchTimes.Count; index++)
        {
            var start = dispatchTimes[index];
            var nextDispatch = index + 1 < dispatchTimes.Count ? dispatchTimes[index + 1] : (DateTimeOffset?)null;
            var verification = verifications
                .FirstOrDefault(item => item.CompletedAt >= start &&
                    (nextDispatch is null || item.CompletedAt <= nextDispatch.Value));
            var end = verification?.CompletedAt ?? nextDispatch;
            if (end is null)
            {
                continue;
            }

            attempts.Add(new AttemptTiming(start, nextDispatch, verification, end.Value));
        }

        return attempts;
    }

    private static DateTimeOffset ResolveAttemptStart(
        TaskSpec task,
        AttemptTiming attempt,
        IReadOnlyList<DateTimeOffset> dispatchTimes)
    {
        if (task.LastProcess is { } process &&
            attempt.DispatchAt == dispatchTimes[^1] &&
            process.StartedAt >= attempt.DispatchAt &&
            process.StartedAt <= attempt.End)
        {
            return process.StartedAt;
        }

        return attempt.DispatchAt;
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> durations)
    {
        var total = TimeSpan.Zero;
        foreach (var duration in durations)
        {
            total += duration;
        }

        return total;
    }

    private static TimeSpan PositiveDuration(DateTimeOffset end, DateTimeOffset start)
    {
        var duration = end - start;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static TimeSpan? Percentile(IReadOnlyList<TimeSpan> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private sealed record AttemptTiming(
        DateTimeOffset DispatchAt,
        DateTimeOffset? NextDispatchAt,
        TaskVerificationRecord? Verification,
        DateTimeOffset End);
}
