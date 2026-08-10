using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalTimingReportSnapshot(
    GoalId GoalId,
    string Objective,
    DateTimeOffset? GoalE2EStartedAt,
    string GoalE2EStartSource,
    DateTimeOffset? GoalE2EEndedAt,
    string GoalE2EEndSource,
    DateTimeOffset? BacklogIntentAt,
    string? BacklogIntentSource,
    DateTimeOffset? IntakeAt,
    DateTimeOffset? FirstDispatchAt,
    DateTimeOffset? LandedAt,
    string? LandingSource,
    TimeSpan BacklogIntentWait,
    TimeSpan IntakeToFirstDispatchWait,
    TimeSpan GateDuration,
    TimeSpan LandingWait,
    TimeSpan WorkDuration,
    TimeSpan WaitDuration,
    TimeSpan TotalDuration,
    double WorkPercent,
    double WaitPercent,
    IReadOnlyList<GoalTimingTaskReport> Tasks,
    GoalHoldTimingReport? CurrentHold);

public sealed record GoalTimingReportContext(
    DateTimeOffset? BacklogIntentAt = null,
    DateTimeOffset? LandedAt = null,
    string? LandingSource = null,
    IReadOnlyList<GoalTimingGateSpan>? GateSpans = null,
    DateTimeOffset? AsOf = null,
    string? BacklogIntentSource = null,
    DateTimeOffset? GoalE2EStartedAt = null,
    string? GoalE2EStartSource = null,
    DateTimeOffset? GoalE2EEndedAt = null,
    string? GoalE2EEndSource = null);

public sealed record GoalHoldTimingReport(
    string State,
    string Blocker,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    DateTimeOffset? StalledAt);

public sealed record GoalTimingGateSpan(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string Outcome);

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
    TimeSpan HandoffWait,
    DispatchOutcomeKind? OutcomeVerdict,
    DispatchRoundValueClass ValueClass,
    string ValueEvidence,
    string WasteSource);

public enum DispatchRoundValueClass
{
    Productive,
    Corrective,
    WastedEnvironmental,
    WastedFalseFail,
    Superseded
}

public enum GoalTerminalOutcome
{
    Active,
    Landed,
    Abandoned,
    Parked
}

public sealed record DispatchValueReportSnapshot(
    DateTimeOffset? Since,
    int GoalCount,
    int DispatchRoundCount,
    IReadOnlyList<DispatchValueGoalReport> Goals,
    IReadOnlyList<DispatchValueWasteSource> TopWasteSources);

public sealed record DispatchValueGoalReport(
    GoalId GoalId,
    string Objective,
    GoalTerminalOutcome TerminalOutcome,
    int DispatchRoundCount,
    int ProductiveCount,
    int CorrectiveCount,
    int WastedEnvironmentalCount,
    int WastedFalseFailCount,
    int SupersededCount,
    IReadOnlyList<GoalTimingRoundReport> Rounds);

public sealed record DispatchValueWasteSource(
    string Source,
    int Count);

public sealed record GoalTimingRollupSnapshot(
    int GoalCount,
    TimeSpan TotalDuration,
    TimeSpan WorkDuration,
    TimeSpan WaitDuration,
    double WorkPercent,
    double WaitPercent,
    int ProductiveCount,
    int CorrectiveCount,
    int WastedEnvironmentalCount,
    int WastedFalseFailCount,
    int SupersededCount,
    IReadOnlyList<GoalTimingPhaseRollup> Phases,
    IReadOnlyList<GoalTimingDailyRollup> Daily,
    IReadOnlyList<DispatchValueWasteSource> TopWasteSources);

public sealed record GoalTimingPhaseRollup(
    string Phase,
    TimeSpan Total,
    double Share,
    TimeSpan Median,
    TimeSpan P90);

public sealed record GoalTimingDailyRollup(
    DateOnly Day,
    int GoalCount,
    TimeSpan TotalDuration,
    TimeSpan MedianTotalDuration,
    double WorkPercent,
    int WastedEnvironmentalCount,
    int WastedFalseFailCount);

public static class GoalTimingReport
{
    public static GoalTimingReportSnapshot Build(Goal goal, GoalTimingReportContext? context = null)
    {
        context ??= new GoalTimingReportContext();
        var intakeAt = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.TaskDelegated)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .Min();
        var dispatchEvents = goal.Timeline
            .Where(evt => evt.TaskId is not null && evt.Kind == ProgressKind.TaskDispatchRecorded)
            .OrderBy(evt => evt.OccurredAt)
            .ToList();
        var firstDispatchAt = dispatchEvents.Select(evt => (DateTimeOffset?)evt.OccurredAt).Min();
        var goalStart = ResolveGoalE2EStart(goal, context, intakeAt);
        var landing = ResolveLanding(goal, context);
        var goalEnd = ResolveGoalE2EEnd(goal, context, landing);
        var roundsByTask = BuildRounds(goal, dispatchEvents, goalEnd.At);
        var allRounds = roundsByTask
            .SelectMany(task => task.Rounds)
            .OrderBy(round => round.DispatchAt)
            .ToList();
        var gateDuration = ResolveGateDuration(goal, context);
        var backlogIntentWait = PositiveDuration(goalStart.At, context.BacklogIntentAt);
        var intakeWait = PositiveDuration(firstDispatchAt, intakeAt);
        var handoffWait = AddHandoffWaits(allRounds);
        var totalDuration = PositiveDuration(goalEnd.At, goalStart.At);
        var observedWorkDuration = allRounds.Aggregate(
            TimeSpan.Zero,
            (sum, round) => sum + round.SandboxPrepDuration + round.WorkerRunDuration) + gateDuration;
        var workDuration = observedWorkDuration <= totalDuration ? observedWorkDuration : totalDuration;
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
        var currentHold = goal.CurrentHold is null
            ? null
            : new GoalHoldTimingReport(
                goal.CurrentHold.State,
                goal.CurrentHold.Blocker,
                goal.CurrentHold.StartedAt,
                PositiveDuration(goalEnd.At, goal.CurrentHold.StartedAt),
                goal.CurrentHold.StalledAt);

        return new GoalTimingReportSnapshot(
            goal.Id,
            goal.Objective,
            goalStart.At,
            goalStart.Source ?? "unavailable",
            goalEnd.At,
            goalEnd.Source ?? "unavailable",
            context.BacklogIntentAt,
            context.BacklogIntentSource,
            intakeAt,
            firstDispatchAt,
            landing.At,
            landing.Source,
            backlogIntentWait,
            intakeWait,
            gateDuration,
            landingWait,
            workDuration,
            waitDuration,
            totalDuration,
            totalDuration > TimeSpan.Zero ? workDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            totalDuration > TimeSpan.Zero ? waitDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            taskReports,
            currentHold);
    }

    public static GoalTimingRollupSnapshot BuildRollup(
        IEnumerable<Goal> goals,
        IReadOnlyDictionary<GoalId, GoalTimingReportContext>? contexts = null)
    {
        contexts ??= new Dictionary<GoalId, GoalTimingReportContext>();
        var items = goals
            .Select(goal =>
            {
                contexts.TryGetValue(goal.Id, out var context);
                return new
                {
                    Goal = goal,
                    Timing = Build(goal, context)
                };
            })
            .Where(item =>
                ResolveTerminalOutcome(item.Goal) == GoalTerminalOutcome.Landed &&
                item.Timing.LandedAt is not null)
            .ToList();

        var phaseObservations = new List<PhaseObservation>();
        foreach (var item in items)
        {
            AddPhase(phaseObservations, "UpstreamBacklogIntentWait", item.Timing.BacklogIntentWait);
            AddPhase(phaseObservations, "IntakeWait", item.Timing.IntakeToFirstDispatchWait);
            AddPhase(phaseObservations, "Gate", item.Timing.GateDuration);
            AddPhase(phaseObservations, "LandingWait", item.Timing.LandingWait);

            foreach (var roleGroup in item.Timing.Tasks.GroupBy(task => task.Role))
            {
                var role = roleGroup.Key.ToString();
                AddPhase(phaseObservations, $"Prep:{role}", roleGroup.SumDuration(task => task.Rounds, round => round.SandboxPrepDuration));
                AddPhase(phaseObservations, $"Worker:{role}", roleGroup.SumDuration(task => task.Rounds, round => round.WorkerRunDuration));
                AddPhase(phaseObservations, $"Handoff:{role}", roleGroup.SumDuration(task => task.Rounds, round => round.HandoffWait));
            }
        }

        var totalDuration = items.SumDuration(item => item.Timing.TotalDuration);
        var workDuration = items.SumDuration(item => item.Timing.WorkDuration);
        var waitDuration = totalDuration >= workDuration ? totalDuration - workDuration : TimeSpan.Zero;
        var dispatchValue = BuildDispatchValueReport(items.Select(item => item.Goal));
        var phases = phaseObservations
            .GroupBy(item => item.Phase, StringComparer.Ordinal)
            .Select(group =>
            {
                var durations = group.Select(item => item.Duration).OrderBy(duration => duration).ToList();
                var total = durations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration);
                return new GoalTimingPhaseRollup(
                    group.Key,
                    total,
                    totalDuration > TimeSpan.Zero ? total.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
                    Percentile(durations, 0.5) ?? TimeSpan.Zero,
                    Percentile(durations, 0.9) ?? TimeSpan.Zero);
            })
            .OrderByDescending(phase => phase.Total)
            .ThenBy(phase => phase.Phase, StringComparer.Ordinal)
            .ToList();

        var daily = items
            .GroupBy(item => DateOnly.FromDateTime(item.Timing.LandedAt!.Value.UtcDateTime))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var dayItems = group.ToList();
                var dayTotal = dayItems.SumDuration(item => item.Timing.TotalDuration);
                var dayWork = dayItems.SumDuration(item => item.Timing.WorkDuration);
                var dayRounds = dayItems
                    .SelectMany(item => item.Timing.Tasks)
                    .SelectMany(task => task.Rounds)
                    .ToList();
                return new GoalTimingDailyRollup(
                    group.Key,
                    dayItems.Count,
                    dayTotal,
                    Percentile(dayItems.Select(item => item.Timing.TotalDuration).OrderBy(duration => duration).ToList(), 0.5) ?? TimeSpan.Zero,
                    dayTotal > TimeSpan.Zero ? dayWork.TotalMilliseconds / dayTotal.TotalMilliseconds : 0.0,
                    dayRounds.Count(round => round.ValueClass == DispatchRoundValueClass.WastedEnvironmental),
                    dayRounds.Count(round => round.ValueClass == DispatchRoundValueClass.WastedFalseFail));
            })
            .ToList();

        return new GoalTimingRollupSnapshot(
            items.Count,
            totalDuration,
            workDuration,
            waitDuration,
            totalDuration > TimeSpan.Zero ? workDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            totalDuration > TimeSpan.Zero ? waitDuration.TotalMilliseconds / totalDuration.TotalMilliseconds : 0.0,
            dispatchValue.Goals.Sum(goal => goal.ProductiveCount),
            dispatchValue.Goals.Sum(goal => goal.CorrectiveCount),
            dispatchValue.Goals.Sum(goal => goal.WastedEnvironmentalCount),
            dispatchValue.Goals.Sum(goal => goal.WastedFalseFailCount),
            dispatchValue.Goals.Sum(goal => goal.SupersededCount),
            phases,
            daily,
            dispatchValue.TopWasteSources);
    }

    public static DispatchValueReportSnapshot BuildDispatchValueReport(
        IEnumerable<Goal> goals,
        DateTimeOffset? since = null)
    {
        var reports = goals
            .Select(goal => new { Goal = goal, Timing = Build(goal) })
            .Select(item => BuildDispatchValueGoalReport(
                item.Goal,
                item.Timing,
                since is null
                    ? item.Timing.Tasks.SelectMany(task => task.Rounds).ToList()
                    : item.Timing.Tasks
                        .SelectMany(task => task.Rounds)
                        .Where(round => round.DispatchAt >= since.Value)
                        .ToList()))
            .Where(report => report.DispatchRoundCount > 0)
            .OrderBy(report => report.Rounds.Min(round => round.DispatchAt))
            .ToList();

        var topWasteSources = reports
            .SelectMany(report => report.Rounds)
            .Where(round => round.ValueClass is DispatchRoundValueClass.WastedEnvironmental or DispatchRoundValueClass.WastedFalseFail)
            .GroupBy(round => string.IsNullOrWhiteSpace(round.WasteSource) ? round.ValueClass.ToString() : round.WasteSource)
            .Select(group => new DispatchValueWasteSource(group.Key, group.Count()))
            .OrderByDescending(source => source.Count)
            .ThenBy(source => source.Source, StringComparer.Ordinal)
            .Take(8)
            .ToList();

        return new DispatchValueReportSnapshot(
            since,
            reports.Count,
            reports.Sum(report => report.DispatchRoundCount),
            reports,
            topWasteSources);
    }

    private static IReadOnlyList<TaskRounds> BuildRounds(
        Goal goal,
        IReadOnlyList<ProgressEvent> dispatchEvents,
        DateTimeOffset? reportEnd)
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
                var verification = FindRoundVerification(verifications, usedVerifications, dispatch, nextTaskDispatch);
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
                var runtimeEndedAt = completedAt ?? Earlier(nextTaskDispatch, reportEnd);
                var laterSuccess = verifications.Any(candidate =>
                    candidate.CompletedAt > (verification?.CompletedAt ?? dispatch.OccurredAt) &&
                    DispatchFailureClassifier.Classify(task, candidate).Kind == DispatchOutcomeKind.VerifiedSuccess);
                var value = ClassifyValue(goal, task, verification, laterSuccess);
                rounds.Add((task.Id, new GoalTimingRoundReport(
                    task.Id,
                    index + 1,
                    dispatch.OccurredAt,
                    processStartedAt,
                    completedAt,
                    ParseSandboxPrepDuration(verification),
                    PositiveDuration(runtimeEndedAt, processStartedAt),
                    TimeSpan.Zero,
                    value.OutcomeVerdict,
                    value.ValueClass,
                    value.Evidence,
                    value.WasteSource)));
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
        ProgressEvent dispatch,
        DateTimeOffset? nextDispatchAt)
    {
        var candidates = verifications.Where(verification =>
            !usedVerifications.Contains(verification) &&
            verification.CompletedAt >= dispatch.OccurredAt &&
            (nextDispatchAt is null || verification.CompletedAt < nextDispatchAt.Value))
            .ToList();

        var dispatchCommand = ExtractDispatchCommand(dispatch.Message);
        if (!string.IsNullOrWhiteSpace(dispatchCommand))
        {
            var commandMatch = candidates.FirstOrDefault(verification =>
                string.Equals(verification.Command, dispatchCommand, StringComparison.Ordinal));
            if (commandMatch is not null)
            {
                return commandMatch;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static string? ExtractDispatchCommand(string message)
    {
        var separator = message.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? null : message[(separator + 2)..];
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

    private static TimeSpan ResolveGateDuration(Goal goal, GoalTimingReportContext context)
    {
        if (context.GateSpans is { Count: > 0 })
        {
            return context.GateSpans.Aggregate(
                TimeSpan.Zero,
                (sum, span) => sum + PositiveDuration(span.EndedAt, span.StartedAt));
        }

        return ParseGateDuration(goal.Tasks.SelectMany(task => task.VerificationHistory));
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

    private static TimingBoundary ResolveGoalE2EStart(
        Goal goal,
        GoalTimingReportContext context,
        DateTimeOffset? intakeAt)
    {
        if (context.GoalE2EStartedAt is { } explicitStart)
        {
            return new TimingBoundary(explicitStart, context.GoalE2EStartSource ?? "context");
        }

        if (intakeAt is { } activationAt)
        {
            return new TimingBoundary(activationAt, "task-delegated");
        }

        var createdAt = goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.GoalCreated)
            .OrderBy(evt => evt.OccurredAt)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .FirstOrDefault();
        if (createdAt is not null)
        {
            return new TimingBoundary(createdAt, "goal-created");
        }

        return goal.MetadataCreatedAt is { } metadataCreatedAt
            ? new TimingBoundary(metadataCreatedAt, "terminal-metadata-created")
            : new TimingBoundary(null, "unavailable");
    }

    private static TimingBoundary ResolveLanding(Goal goal, GoalTimingReportContext context)
    {
        if (context.LandedAt is { } landedAt)
        {
            return new TimingBoundary(landedAt, context.LandingSource ?? "context");
        }

        return goal is { IsMetadataOnly: true, Status: GoalStatus.Completed, MetadataTerminatedAt: { } metadataTerminatedAt }
            ? new TimingBoundary(metadataTerminatedAt, "terminal-metadata")
            : new TimingBoundary(null, null);
    }

    private static TimingBoundary ResolveGoalE2EEnd(
        Goal goal,
        GoalTimingReportContext context,
        TimingBoundary landing)
    {
        if (context.GoalE2EEndedAt is { } explicitEnd)
        {
            return new TimingBoundary(explicitEnd, context.GoalE2EEndSource ?? "context");
        }

        if (landing.At is not null)
        {
            return new TimingBoundary(landing.At, landing.Source ?? "landing-evidence");
        }

        if (goal.IsTerminal && goal.MetadataTerminatedAt is { } metadataTerminatedAt)
        {
            return new TimingBoundary(metadataTerminatedAt, "terminal-metadata");
        }

        if (goal.IsTerminal)
        {
            var terminalTimelineAt = goal.Timeline
                .OrderByDescending(evt => evt.OccurredAt)
                .Select(evt => (DateTimeOffset?)evt.OccurredAt)
                .FirstOrDefault();
            if (terminalTimelineAt is not null)
            {
                return new TimingBoundary(terminalTimelineAt, "terminal-timeline");
            }
        }

        if (context.AsOf is { } asOf)
        {
            return new TimingBoundary(asOf, "sample-time");
        }

        var latestRecordedAt = goal.Timeline
            .OrderByDescending(evt => evt.OccurredAt)
            .Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .FirstOrDefault();
        return latestRecordedAt is not null
            ? new TimingBoundary(latestRecordedAt, "latest-recorded-event")
            : new TimingBoundary(null, "unavailable");
    }

    private static GoalTerminalOutcome ResolveTerminalOutcome(Goal goal) =>
        goal.Status switch
        {
            GoalStatus.Completed or GoalStatus.Verified => GoalTerminalOutcome.Landed,
            GoalStatus.Parked => GoalTerminalOutcome.Parked,
            GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded => GoalTerminalOutcome.Abandoned,
            _ => GoalTerminalOutcome.Active
        };

    private static DispatchValueGoalReport BuildDispatchValueGoalReport(
        Goal goal,
        GoalTimingReportSnapshot timing,
        IReadOnlyList<GoalTimingRoundReport> rounds)
    {
        return new DispatchValueGoalReport(
            timing.GoalId,
            timing.Objective,
            ResolveTerminalOutcome(goal),
            rounds.Count,
            rounds.Count(round => round.ValueClass == DispatchRoundValueClass.Productive),
            rounds.Count(round => round.ValueClass == DispatchRoundValueClass.Corrective),
            rounds.Count(round => round.ValueClass == DispatchRoundValueClass.WastedEnvironmental),
            rounds.Count(round => round.ValueClass == DispatchRoundValueClass.WastedFalseFail),
            rounds.Count(round => round.ValueClass == DispatchRoundValueClass.Superseded),
            rounds.OrderBy(round => round.DispatchAt).ToList());
    }

    private static DispatchValueClassification ClassifyValue(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord? verification,
        bool laterSuccess)
    {
        var terminal = ResolveTerminalOutcome(goal);
        if (verification is null)
        {
            return terminal == GoalTerminalOutcome.Abandoned
                ? new(null, DispatchRoundValueClass.Superseded, $"goal.Status={goal.Status}; no verification paired to dispatch round", "superseded")
                : new(null, DispatchRoundValueClass.Corrective, "no verification paired to dispatch round; pending/active work remains auditable from dispatch timeline", "unpaired-dispatch");
        }

        var outcome = DispatchFailureClassifier.Classify(task, verification);
        var receipt = string.IsNullOrWhiteSpace(outcome.ClassifierReceipt)
            ? $"classifier={outcome.Kind}"
            : outcome.ClassifierReceipt;
        var evidence = $"verification.ExitCode={verification.ExitCode}; verification.CompletedAt={verification.CompletedAt:u}; {receipt}; evidence={outcome.EvidenceSummary}";

        if (IsFalseFailBridge(verification, laterSuccess, terminal))
        {
            return new(outcome.Kind, DispatchRoundValueClass.WastedFalseFail, evidence, "false-file-change-guard");
        }

        if (IsEnvironmentalWaste(outcome.Kind))
        {
            return new(outcome.Kind, DispatchRoundValueClass.WastedEnvironmental, evidence, outcome.Kind.ToString());
        }

        if (terminal == GoalTerminalOutcome.Abandoned)
        {
            return new(outcome.Kind, DispatchRoundValueClass.Superseded, $"{evidence}; goal.Status={goal.Status}", "abandoned-or-superseded-goal");
        }

        if (outcome.Kind == DispatchOutcomeKind.VerifiedSuccess)
        {
            return new(outcome.Kind, DispatchRoundValueClass.Productive, evidence, string.Empty);
        }

        return new(outcome.Kind, DispatchRoundValueClass.Corrective, $"{evidence}; laterSuccess={laterSuccess}; goal.Status={goal.Status}", string.Empty);
    }

    private static bool IsEnvironmentalWaste(DispatchOutcomeKind kind) =>
        kind is DispatchOutcomeKind.ProviderConnectivity
            or DispatchOutcomeKind.ProviderAuthentication
            or DispatchOutcomeKind.ProviderModelRejection
            or DispatchOutcomeKind.PreflightFailure
            or DispatchOutcomeKind.LaunchFailure
            or DispatchOutcomeKind.EmptyOutputFlake
            or DispatchOutcomeKind.RecoverableSubscriptionLimit;

    private static bool IsFalseFailBridge(
        TaskVerificationRecord verification,
        bool laterSuccess,
        GoalTerminalOutcome terminal) =>
        (verification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.OrdinalIgnoreCase) ||
         verification.StandardError.Contains("did not produce relevant file-change evidence", StringComparison.OrdinalIgnoreCase)) &&
        (laterSuccess || terminal == GoalTerminalOutcome.Landed);

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

    private static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first <= second ? first : second;
    }

    private sealed record DispatchValueClassification(
        DispatchOutcomeKind? OutcomeVerdict,
        DispatchRoundValueClass ValueClass,
        string Evidence,
        string WasteSource);

    private sealed record TimingBoundary(DateTimeOffset? At, string? Source);

    private sealed record TaskRounds(TaskId TaskId, IReadOnlyList<GoalTimingRoundReport> Rounds);

    private sealed record PhaseObservation(string Phase, TimeSpan Duration);

    private static void AddPhase(List<PhaseObservation> observations, string phase, TimeSpan duration) =>
        observations.Add(new PhaseObservation(phase, duration > TimeSpan.Zero ? duration : TimeSpan.Zero));

    private static TimeSpan SumDuration<T>(this IEnumerable<T> items, Func<T, TimeSpan> selector) =>
        items.Aggregate(TimeSpan.Zero, (sum, item) => sum + selector(item));

    private static TimeSpan SumDuration<TItem, TNested>(
        this IEnumerable<TItem> items,
        Func<TItem, IEnumerable<TNested>> nestedSelector,
        Func<TNested, TimeSpan> durationSelector) =>
        items.SelectMany(nestedSelector).Aggregate(TimeSpan.Zero, (sum, item) => sum + durationSelector(item));

    private static TimeSpan? Percentile(List<TimeSpan> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}
