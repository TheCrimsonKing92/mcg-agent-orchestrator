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
    int FailedAttemptCount,
    int RealFailureAttemptCount,
    int EnvironmentalFailureAttemptCount,
    int ManufacturedFixedFailureAttemptCount,
    int UnknownEraFailureAttemptCount,
    int FindingAttemptCount = 0);

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
    double FailureRate,
    int RealFailureAttemptCount = 0,
    int EnvironmentalFailureAttemptCount = 0,
    int ManufacturedFixedFailureAttemptCount = 0,
    int UnknownEraFailureAttemptCount = 0,
    double RealFailureRate = 0.0,
    double EnvironmentalFailureRate = 0.0,
    double ManufacturedFixedFailureRate = 0.0,
    double UnknownEraFailureRate = 0.0,
    int FindingAttemptCount = 0)
{
    public double AttemptsPerTask => TaskCount > 0 ? (double)AttemptCount / TaskCount : 0.0;

    public bool HasPublishedStats => TaskCount >= TaskDurationReport.MinSamplesForPublishedStats &&
        MedianLegitimateRuntime is not null;

    public string Scope => string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(ModelName)
        ? $"{Role}/{Complexity}"
        : $"{Role}/{Complexity} {ProviderName}/{ModelName}";
}

public sealed record TaskDurationTrendRecord(
    DateOnly Day,
    int TaskCount,
    int AttemptCount,
    int FailedAttemptCount,
    double AttemptsPerTask,
    double FailureRate,
    int FindingAttemptCount = 0);

public static class TaskDurationReport
{
    public const int MinSamplesForPublishedStats = 3;

    public static IReadOnlyList<TaskDurationStatsRecord> BuildByRoleAndComplexity(
        IEnumerable<Goal> goals,
        DateTimeOffset? since = null) =>
        Build(CollectObservations(goals, since), includeModel: false);

    public static IReadOnlyList<TaskDurationStatsRecord> BuildByRoleComplexityAndModel(
        IEnumerable<Goal> goals,
        DateTimeOffset? since = null) =>
        Build(CollectObservations(goals, since), includeModel: true);

    public static IReadOnlyList<TaskDurationTrendRecord> BuildDailyTrend(IEnumerable<Goal> goals, DateTimeOffset? since = null)
    {
        return CollectAttemptObservations(goals, since)
            .GroupBy(attempt => DateOnly.FromDateTime(attempt.DispatchAt.UtcDateTime))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var attempts = group.ToList();
                var taskCount = attempts
                    .Select(attempt => (attempt.GoalId, attempt.TaskId))
                    .Distinct()
                    .Count();
                var attemptCount = attempts.Count;
                var failedAttemptCount = attempts.Count(attempt => !attempt.Succeeded && attempt.OutcomeClass != TaskOutcomeClass.Finding);

                return new TaskDurationTrendRecord(
                    group.Key,
                    taskCount,
                    attemptCount,
                    failedAttemptCount,
                    taskCount > 0 ? (double)attemptCount / taskCount : 0.0,
                    attemptCount > 0 ? (double)failedAttemptCount / attemptCount : 0.0,
                    attempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.Finding));
            })
            .ToList();
    }

    public static IReadOnlyList<TaskDurationObservation> CollectObservations(
        IEnumerable<Goal> goals,
        DateTimeOffset? since = null)
    {
        return CollectAttemptObservations(goals, since)
            .GroupBy(attempt => (
                attempt.GoalId,
                attempt.TaskId,
                attempt.Role,
                attempt.Complexity,
                attempt.ProviderName,
                attempt.ModelName))
            .Select(group =>
            {
                var attempts = group
                    .OrderBy(attempt => attempt.DispatchAt)
                    .ThenBy(attempt => attempt.EndedAt)
                    .ToList();
                var finalSuccessfulAttempt = attempts
                    .Where(attempt => attempt.Succeeded)
                    .LastOrDefault();
                var failedAttempts = attempts
                    .Where(attempt => attempt.OutcomeClass is not (TaskOutcomeClass.Success or TaskOutcomeClass.Finding))
                    .ToList();

                return new TaskDurationObservation(
                    group.Key.GoalId,
                    group.Key.TaskId,
                    group.Key.Role,
                    group.Key.Complexity,
                    group.Key.ProviderName,
                    group.Key.ModelName,
                    finalSuccessfulAttempt?.LegitimateRuntime,
                    failedAttempts.Aggregate(TimeSpan.Zero, (sum, attempt) => sum + attempt.FailureInterventionOverhead),
                    attempts.Count,
                    failedAttempts.Count,
                    failedAttempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.RealFailure),
                    failedAttempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.Environmental),
                    failedAttempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.ManufacturedFixed),
                    failedAttempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.UnknownEra),
                    attempts.Count(attempt => attempt.OutcomeClass == TaskOutcomeClass.Finding));
            })
            .ToList();
    }

    private static List<TaskDurationAttemptObservation> CollectAttemptObservations(
        IEnumerable<Goal> goals,
        DateTimeOffset? since)
    {
        var observations = new List<TaskDurationAttemptObservation>();
        foreach (var goal in goals)
        {
            foreach (var task in goal.Tasks)
            {
                observations.AddRange(BuildAttemptObservations(goal, task, since));
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
            .Where(record => record.HasPublishedStats)
            .OrderByDescending(record => record.TaskCount)
            .ThenBy(record => record.Scope, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static List<TaskDurationStatsRecord> Build(
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
                    .OrderBy(duration => duration)
                    .ToList();
                var attempts = items.Sum(item => item.AttemptCount);
                var failedAttempts = items.Sum(item => item.FailedAttemptCount);
                var realFailures = items.Sum(item => item.RealFailureAttemptCount);
                var environmentalFailures = items.Sum(item => item.EnvironmentalFailureAttemptCount);
                var manufacturedFailures = items.Sum(item => item.ManufacturedFixedFailureAttemptCount);
                var unknownEraFailures = items.Sum(item => item.UnknownEraFailureAttemptCount);

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
                    attempts > 0 ? (double)failedAttempts / attempts : 0.0,
                    realFailures,
                    environmentalFailures,
                    manufacturedFailures,
                    unknownEraFailures,
                    attempts > 0 ? (double)realFailures / attempts : 0.0,
                    attempts > 0 ? (double)environmentalFailures / attempts : 0.0,
                    attempts > 0 ? (double)manufacturedFailures / attempts : 0.0,
                    attempts > 0 ? (double)unknownEraFailures / attempts : 0.0,
                    items.Sum(item => item.FindingAttemptCount));
            })
            .ToList();
    }

    private static List<TaskDurationAttemptObservation> BuildAttemptObservations(
        Goal goal,
        TaskSpec task,
        DateTimeOffset? since)
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
            return [];
        }

        var cancellationTimes = goal.Timeline
            .Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCancelled)
            .Select(evt => evt.OccurredAt)
            .OrderBy(timestamp => timestamp)
            .ToList();
        var verifications = task.VerificationHistory
            .OrderBy(verification => verification.CompletedAt)
            .ToList();
        var attempts = PairAttempts(dispatchTimes, verifications, cancellationTimes);
        if (attempts.Count == 0)
        {
            return [];
        }

        var complexity = task.LastDispatch?.TaskComplexity is { } recorded and not TaskComplexity.Auto
            ? recorded
            : TaskComplexityEstimator.Estimate(task.Description, goal.Objective, task.RequiredRole);

        var observations = new List<TaskDurationAttemptObservation>();
        foreach (var attempt in attempts.Where(attempt => since is null || attempt.DispatchAt >= since.Value))
        {
            var start = ResolveAttemptStart(task, attempt, dispatchTimes);
            var recordedClass = ResolveAttemptOutcomeClass(goal, task, attempt);
            var succeeded = attempt.Verification?.Succeeded is true && recordedClass != TaskOutcomeClass.Finding;
            var outcomeClass = recordedClass == TaskOutcomeClass.Finding
                ? TaskOutcomeClass.Finding
                : succeeded
                ? TaskOutcomeClass.Success
                : recordedClass;
            var legitimateRuntime = succeeded ? PositiveDuration(attempt.End, start) : (TimeSpan?)null;
            var overhead = TimeSpan.Zero;
            if (!succeeded && outcomeClass != TaskOutcomeClass.Finding)
            {
                overhead += PositiveDuration(attempt.End, start);
                if (attempt.NextDispatchAt is not null)
                {
                    overhead += PositiveDuration(attempt.NextDispatchAt.Value, attempt.End);
                }
            }

            observations.Add(new TaskDurationAttemptObservation(
                goal.Id.Value,
                task.Id.Value,
                task.RequiredRole,
                complexity,
                task.LastDispatch?.ProviderName,
                task.LastDispatch?.ModelName,
                attempt.DispatchAt,
                attempt.End,
                succeeded,
                outcomeClass,
                legitimateRuntime,
                overhead));
        }

        if (task.LastProcess is { WasCancelled: true, CompletedAt: { } cancelledAt } process &&
            !attempts.Any(attempt => attempt.End == cancelledAt) &&
            (since is null || process.StartedAt >= since.Value))
        {
            observations.Add(new TaskDurationAttemptObservation(
                goal.Id.Value,
                task.Id.Value,
                task.RequiredRole,
                complexity,
                task.LastDispatch?.ProviderName,
                task.LastDispatch?.ModelName,
                process.StartedAt,
                cancelledAt,
                Succeeded: false,
                TaskOutcomeClass.Environmental,
                LegitimateRuntime: null,
                PositiveDuration(cancelledAt, process.StartedAt)));
        }

        return observations;
    }

    private static TaskOutcomeClass ResolveAttemptOutcomeClass(Goal goal, TaskSpec task, AttemptTiming attempt)
    {
        var classification = TaskOutcomeClassifier.FromTimeline(
            goal.Timeline,
            task.Id,
            WorkTaskStatus.Failed,
            attempt.Verification?.CompletedAt ?? attempt.End,
            attempt.NextDispatchAt);
        return classification.Class;
    }

    private static List<AttemptTiming> PairAttempts(
        List<DateTimeOffset> dispatchTimes,
        IReadOnlyList<TaskVerificationRecord> verifications,
        IReadOnlyList<DateTimeOffset> cancellationTimes)
    {
        var attempts = new List<AttemptTiming>();
        for (var index = 0; index < dispatchTimes.Count; index++)
        {
            var start = dispatchTimes[index];
            var nextDispatch = index + 1 < dispatchTimes.Count ? dispatchTimes[index + 1] : (DateTimeOffset?)null;
            var verification = verifications
                .FirstOrDefault(item => item.CompletedAt >= start &&
                    (nextDispatch is null || item.CompletedAt <= nextDispatch.Value));
            var cancellation = cancellationTimes
                .FirstOrDefault(item => item >= start &&
                    (nextDispatch is null || item <= nextDispatch.Value));
            var end = Earliest(verification?.CompletedAt, cancellation == default ? null : cancellation, nextDispatch);
            if (end is null)
            {
                continue;
            }

            attempts.Add(new AttemptTiming(start, nextDispatch, verification, end.Value));
        }

        return attempts;
    }

    private static DateTimeOffset? Earliest(params DateTimeOffset?[] timestamps)
    {
        DateTimeOffset? earliest = null;
        foreach (var timestamp in timestamps)
        {
            if (timestamp is null)
            {
                continue;
            }

            if (earliest is null || timestamp.Value < earliest.Value)
            {
                earliest = timestamp.Value;
            }
        }

        return earliest;
    }

    private static DateTimeOffset ResolveAttemptStart(
        TaskSpec task,
        AttemptTiming attempt,
        List<DateTimeOffset> dispatchTimes)
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

    private static TimeSpan PositiveDuration(DateTimeOffset end, DateTimeOffset start)
    {
        var duration = end - start;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static TimeSpan? Percentile(List<TimeSpan> sorted, double percentile)
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

    private sealed record TaskDurationAttemptObservation(
        string GoalId,
        string TaskId,
        AgentRole Role,
        TaskComplexity Complexity,
        string? ProviderName,
        string? ModelName,
        DateTimeOffset DispatchAt,
        DateTimeOffset EndedAt,
        bool Succeeded,
        TaskOutcomeClass OutcomeClass,
        TimeSpan? LegitimateRuntime,
        TimeSpan FailureInterventionOverhead);
}
