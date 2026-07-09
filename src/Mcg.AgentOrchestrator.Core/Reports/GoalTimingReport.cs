using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalTimingReportSnapshot(
    GoalId GoalId,
    string Objective,
    DateTimeOffset? IntakeAt,
    DateTimeOffset? FirstDispatchAt,
    DateTimeOffset? LandedAt,
    TimeSpan IntakeToFirstDispatchWait,
    TimeSpan GateDuration,
    TimeSpan LandingWait,
    TimeSpan WorkDuration,
    TimeSpan WaitDuration,
    TimeSpan TotalDuration,
    double WorkPercent,
    double WaitPercent,
    IReadOnlyList<GoalTimingTaskReport> Tasks);

public sealed record GoalTimingTaskReport(
    TaskId TaskId,
    AgentRole Role,
    string Description,
    TimeSpan IntakeToFirstDispatchWait,
    IReadOnlyList<GoalTimingRoundReport> Rounds);

public sealed record GoalTimingRoundReport(
    TaskId TaskId,
    int RoundNumber,
    DateTimeOffset DispatchAt,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset? CompletedAt,
    TimeSpan SandboxPrepDuration,
    TimeSpan WorkerRunDuration,
    TimeSpan HandoffWait);

public static class GoalTimingReport
{
    public static GoalTimingReportSnapshot Build(Goal goal)
    {
        var intakeAt = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.TaskDelegated)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .Min();
        var dispatchEvents = goal.Timeline
            .Where(evt => evt.TaskId is not null && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
        var firstDispatchAt = dispatchEvents.Select(evt => (DateTimeOffset?)evt.OccurredAt).Min();
        var landedAt = ResolveLandedAt(goal);
        var roundsByTask = BuildRounds(goal, dispatchEvents);
        var allRounds = roundsByTask
            .SelectMany(task => task.Rounds)
            .OrderBy(round => round.DispatchAt)
            .ToList();
        var gateDuration = ParseGateDuration(goal.Tasks.SelectMany(task => task.VerificationHistory));
        var intakeWait = PositiveDuration(firstDispatchAt, intakeAt);
        var handoffWait = AddHandoffWaits(allRounds);
        var workDuration = allRounds.Aggregate(TimeSpan.Zero, (sum, round) => sum + round.SandboxPrepDuration + round.WorkerRunDuration) + gateDuration;
        var totalDuration = PositiveDuration(landedAt, intakeAt);
        var landingWait = totalDuration - workDuration - intakeWait - handoffWait;
        if (landingWait < TimeSpan.Zero)
        {
            landingWait = TimeSpan.Zero;
        }

        var waitDuration = totalDuration >= workDuration
            ? totalDuration - workDuration
            : TimeSpan.Zero;
        var taskReports = goal.Tasks
            .Select(task => new GoalTimingTaskReport(
                task.Id,
                task.RequiredRole,
                task.Description,
                task.Id == dispatchEvents.FirstOrDefault()?.TaskId ? intakeWait : TimeSpan.Zero,
                roundsByTask.FirstOrDefault(item => item.TaskId == task.Id)?.Rounds ?? []))
            .ToList();

        return new GoalTimingReportSnapshot(
            goal.Id,
            goal.Objective,
            intakeAt,
            firstDispatchAt,
            landedAt,
            intakeWait,
            gateDuration,
            landingWait,
            workDuration,
            waitDuration,
            totalDuration,
            totalDuration > TimeSpan.Zero ? workDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            totalDuration > TimeSpan.Zero ? waitDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            taskReports);
    }

    private static IReadOnlyList<TaskRounds> BuildRounds(Goal goal, IReadOnlyList<ProgressEvent> dispatchEvents)
    {
        var rounds = new List<(TaskId TaskId, GoalTimingRoundReport Round)>();
        foreach (var task in goal.Tasks)
        {
            var taskDispatches = dispatchEvents
                .Where(evt => evt.TaskId == task.Id)
                .OrderBy(evt => evt.OccurredAt)
                .ToList();
            var taskProcessStarts = goal.Timeline
                .Where(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskProcessStarted)
                .OrderBy(evt => evt.OccurredAt)
                .ToList();
            var verifications = task.VerificationHistory
                .OrderBy(verification => verification.CompletedAt)
                .ToList();
            var usedVerifications = new HashSet<TaskVerificationRecord>();

            for (var index = 0; index < taskDispatches.Count; index++)
            {
                var dispatch = taskDispatches[index];
                var nextTaskDispatch = index + 1 < taskDispatches.Count ? taskDispatches[index + 1].OccurredAt : (DateTimeOffset?)null;
                var verification = FindRoundVerification(verifications, usedVerifications, dispatch.OccurredAt, nextTaskDispatch);
                if (verification is not null)
                {
                    usedVerifications.Add(verification);
                }

                var processStartedAt = taskProcessStarts
                    .FirstOrDefault(evt => evt.OccurredAt >= dispatch.OccurredAt &&
                        (verification is null || evt.OccurredAt <= verification.CompletedAt) &&
                        (nextTaskDispatch is null || evt.OccurredAt < nextTaskDispatch.Value))
                    ?.OccurredAt;
                var completedAt = ResolveCompletedAt(task, verification, processStartedAt);
                rounds.Add((task.Id, new GoalTimingRoundReport(
                    task.Id,
                    index + 1,
                    dispatch.OccurredAt,
                    processStartedAt,
                    completedAt,
                    ParseSandboxPrepDuration(verification),
                    PositiveDuration(completedAt, processStartedAt),
                    TimeSpan.Zero)));
            }
        }

        var ordered = rounds
            .OrderBy(item => item.Round.DispatchAt)
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = ordered[index].Round;
            var nextDispatchAt = index + 1 < ordered.Count ? ordered[index + 1].Round.DispatchAt : (DateTimeOffset?)null;
            var handoff = PositiveDuration(nextDispatchAt, current.CompletedAt);
            ordered[index] = (ordered[index].TaskId, current with { HandoffWait = handoff });
        }

        return ordered
            .GroupBy(item => item.TaskId)
            .Select(group => new TaskRounds(
                group.Key,
                group.Select(item => item.Round).OrderBy(round => round.RoundNumber).ToList()))
            .ToList();
    }

    private static TaskVerificationRecord? FindRoundVerification(
        IReadOnlyList<TaskVerificationRecord> verifications,
        HashSet<TaskVerificationRecord> usedVerifications,
        DateTimeOffset dispatchAt,
        DateTimeOffset? nextDispatchAt)
    {
        return verifications.FirstOrDefault(verification =>
            !usedVerifications.Contains(verification) &&
            verification.CompletedAt >= dispatchAt &&
            (nextDispatchAt is null || verification.CompletedAt < nextDispatchAt.Value));
    }

    private static DateTimeOffset? ResolveCompletedAt(
        TaskSpec task,
        TaskVerificationRecord? verification,
        DateTimeOffset? processStartedAt)
    {
        if (task.LastProcess is { CompletedAt: { } processCompletedAt } process &&
            processStartedAt is not null &&
            process.StartedAt == processStartedAt.Value &&
            MatchesArtifactBase(process, verification))
        {
            return processCompletedAt;
        }

        return verification?.CompletedAt;
    }

    private static bool MatchesArtifactBase(TaskProcessRecord process, TaskVerificationRecord? verification)
    {
        if (verification is null)
        {
            return true;
        }

        var processBase = ArtifactBase(process.StandardErrorPath) ?? ArtifactBase(process.StandardOutputPath);
        var verificationBase = ArtifactBase(verification.StandardErrorPath) ?? ArtifactBase(verification.StandardOutputPath);
        return processBase is null || verificationBase is null || string.Equals(processBase, verificationBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ArtifactBase(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        foreach (var suffix in new[] { ".err.log", ".out.log", ".exit.txt", ".log", ".txt" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    private static TimeSpan AddHandoffWaits(IReadOnlyList<GoalTimingRoundReport> rounds) =>
        rounds.Aggregate(TimeSpan.Zero, (sum, round) => sum + round.HandoffWait);

    private static TimeSpan ParseSandboxPrepDuration(TaskVerificationRecord? verification)
    {
        if (verification is null)
        {
            return TimeSpan.Zero;
        }

        return SplitLines(verification.StandardError)
            .Select(TryParseSandboxPrepElapsed)
            .Where(duration => duration is not null)
            .Select(duration => duration!.Value)
            .LastOrDefault();
    }

    private static TimeSpan ParseGateDuration(IEnumerable<TaskVerificationRecord> verifications)
    {
        var total = TimeSpan.Zero;
        foreach (var verification in verifications)
        {
            foreach (var line in SplitLines(verification.StandardOutput).Concat(SplitLines(verification.StandardError)))
            {
                if (!line.Contains("PHASE_TIMING", StringComparison.Ordinal) ||
                    !line.Contains("elapsedMs=", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith("elapsedMs=", StringComparison.Ordinal) &&
                        long.TryParse(part["elapsedMs=".Length..].Trim('"'), out var elapsedMs))
                    {
                        total += TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
                        break;
                    }
                }
            }
        }

        return total;
    }

    private static TimeSpan? TryParseSandboxPrepElapsed(string line)
    {
        if (!line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var evt) ||
                !string.Equals(evt.GetString(), "sandbox-prep", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("phase", out var phase) ||
                phase.GetString() is not ("complete" or "receipt-hit") ||
                !root.TryGetProperty("elapsedMs", out var elapsedMs))
            {
                return null;
            }

            return TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs.GetInt64()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ResolveLandedAt(Goal goal)
    {
        var policyLanding = goal.Timeline
            .Where(evt => evt.TaskId is null && evt.Kind == ProgressKind.GoalPolicyDecision)
            .OrderByDescending(evt => evt.OccurredAt)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .FirstOrDefault();
        return policyLanding ?? goal.Timeline
            .OrderByDescending(evt => evt.OccurredAt)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .FirstOrDefault();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static TimeSpan PositiveDuration(DateTimeOffset? end, DateTimeOffset? start)
    {
        if (end is null || start is null)
        {
            return TimeSpan.Zero;
        }

        var duration = end.Value - start.Value;
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private sealed record TaskRounds(TaskId TaskId, IReadOnlyList<GoalTimingRoundReport> Rounds);
}
