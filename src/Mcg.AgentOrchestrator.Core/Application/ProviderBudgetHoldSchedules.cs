using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderBudgetHoldSchedule(
    ProviderBudgetExhaustionHold Hold, int Attempt, DateTimeOffset? ResetAt, string ResetSource,
    DateTimeOffset NextProbeAt, bool ProbeInFlight, GoalId? AdmittedGoalId, TaskId? AdmittedTaskId,
    string LastProbeOutcome);

/// <summary>A schedule is a projection of binding history; admission notes reserve one real task dispatch.</summary>
public static class ProviderBudgetHoldSchedules
{
    public sealed record ProbeMarker(string BindingKey, DateTimeOffset Due, int Attempt, string ResetSource);
    public sealed record ResetMarker(string BindingKey, DateTimeOffset ResetAt, string IntentId);

    private static readonly Regex ProbePattern = new(
        @"\APROVIDER_BUDGET_PROBE binding=(?<binding>.+) due=(?<at>\S+) attempt=(?<attempt>\d+) reset_source=(?<source>operator|parsed|backoff)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ResetPattern = new(
        @"\APROVIDER_BUDGET_RESET binding=(?<binding>.+) reset_at=(?<at>\S+) source=operator intent=(?<intent>\S+)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex IsoResetPattern = new(
        @"\bresets (?:at|on) (?<at>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))(?![\w:+-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CalendarResetPattern = new(
        @"\bresets (?<month>[A-Za-z]{3}) (?<day>\d{1,2}), (?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<period>am|pm) \((?<zone>[^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string FormatInstant(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static string FormatProbe(ProbeMarker marker) =>
        $"PROVIDER_BUDGET_PROBE binding={marker.BindingKey} due={FormatInstant(marker.Due)} attempt={marker.Attempt} reset_source={marker.ResetSource}";
    public static string FormatReset(ResetMarker marker) =>
        $"PROVIDER_BUDGET_RESET binding={marker.BindingKey} reset_at={FormatInstant(marker.ResetAt)} source=operator intent={marker.IntentId}";

    public static bool TryParseProbe(ProgressEvent evt, out ProbeMarker marker)
    {
        marker = null!;
        if (evt.Kind != ProgressKind.TaskNote) return false;
        var match = ProbePattern.Match(evt.Message);
        if (!match.Success || !TryParseInstant(match.Groups["at"].Value, out var at) ||
            !int.TryParse(match.Groups["attempt"].Value, out var attempt) || attempt < 1) return false;
        marker = new(match.Groups["binding"].Value, at, attempt, match.Groups["source"].Value);
        return true;
    }

    public static bool TryParseReset(ProgressEvent evt, out ResetMarker marker)
    {
        marker = null!;
        if (evt.Kind != ProgressKind.TaskNote) return false;
        var match = ResetPattern.Match(evt.Message);
        if (!match.Success || !TryParseInstant(match.Groups["at"].Value, out var at)) return false;
        marker = new(match.Groups["binding"].Value, at, match.Groups["intent"].Value);
        return true;
    }

    public static bool TryParseInstant(string text, out DateTimeOffset instant)
    {
        instant = default;
        if (!Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant)) return false;
        instant = instant.ToUniversalTime();
        return true;
    }

    public static bool TryGetResetAt(TaskVerificationRecord exhaustion, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (!DispatchFailureClassifier.TryGetProviderBudgetExhaustionLine(exhaustion, out var line)) return false;
        if (DispatchFailureClassifier.TryGetRelativeRetryAfter(line, out var delay))
        {
            try { resetAt = exhaustion.CompletedAt.Add(delay).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return false; }
            return resetAt > exhaustion.CompletedAt;
        }
        var iso = IsoResetPattern.Match(line);
        if (iso.Success && TryParseInstant(iso.Groups["at"].Value, out resetAt))
            return resetAt > exhaustion.CompletedAt;
        var match = CalendarResetPattern.Match(line);
        if (!match.Success) return false;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
            var year = TimeZoneInfo.ConvertTime(exhaustion.CompletedAt, zone).Year;
            var month = DateTime.ParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture).Month;
            var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            if (hour is < 1 or > 12) return false;
            hour = hour % 12 + (match.Groups["period"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var candidates = new List<DateTimeOffset>();
            foreach (var candidateYear in new[] { year - 1, year, year + 1 })
            {
                if (candidateYear is < 1 or > 9999 || day < 1 || day > DateTime.DaysInMonth(candidateYear, month)) continue;
                var local = new DateTime(candidateYear, month, day, hour, minute, 0);
                if (!zone.IsInvalidTime(local) && !zone.IsAmbiguousTime(local))
                    candidates.Add(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)));
            }
            if (candidates.Count == 0) return false;
            resetAt = candidates.MinBy(at => (at - exhaustion.CompletedAt).Duration());
            return resetAt > exhaustion.CompletedAt;
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException)
        {
            return false;
        }
    }

    public static bool TryGetHoldByBindingKey(IEnumerable<Goal> goals, string bindingKey, out ProviderBudgetExhaustionHold hold)
    {
        var all = goals.ToArray();
        foreach (var dispatch in all.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
            if (IsBinding(dispatch, bindingKey) && DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(
                    all, dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory, out hold))
                return true;
        hold = null!;
        return false;
    }

    public static ProviderBudgetHoldSchedule Evaluate(IEnumerable<Goal> goals, ProviderBudgetExhaustionHold hold, DateTimeOffset now)
    {
        var all = goals.ToArray();
        var tasks = all.SelectMany(goal => goal.Tasks).ToArray();
        var dispatches = tasks.SelectMany(task => task.DispatchHistory.Where(dispatch => IsBinding(dispatch, hold.BindingKey))
            .Select(dispatch => (Task: task, Dispatch: dispatch))).ToArray();
        var observations = tasks.SelectMany(task => task.VerificationHistory.Select(verification =>
            (Task: task, Verification: verification, Dispatch: DispatchFailureClassifier.FindDispatchForVerification(task, verification))))
            .Where(item => item.Dispatch is not null && IsBinding(item.Dispatch, hold.BindingKey)).ToArray();
        var exhaustion = observations.Where(item => item.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted)
            .MaxBy(item => item.Verification.CompletedAt).Verification
            ?? throw new InvalidOperationException("A budget hold requires an exhaustion verification on its binding.");
        var recoveryAt = tasks.Where(task => task.LatestProviderBudgetRecoveryAt is not null)
            .Where(task => task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= task.LatestProviderBudgetRecoveryAt) is { } dispatch && IsBinding(dispatch, hold.BindingKey))
            .Select(task => task.LatestProviderBudgetRecoveryAt!.Value)
            .Concat(observations.Where(item => item.Verification.Succeeded).Select(item => item.Verification.CompletedAt))
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var k = Math.Max(1, observations.Count(item => item.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
            item.Verification.CompletedAt > recoveryAt));
        var notes = all.SelectMany(goal => goal.Timeline.Select((evt, index) => (Goal: goal, Event: evt, Index: index))).ToArray();
        var probes = notes.Where(item => TryParseProbe(item.Event, out var marker) && SameBinding(marker.BindingKey, hold.BindingKey))
            .OrderBy(item => item.Event.OccurredAt).ToArray();
        var latestProbe = probes.LastOrDefault();
        var completedProbes = probes.Select(probe =>
            {
                var task = probe.Goal.Tasks.FirstOrDefault(task => task.Id == probe.Event.TaskId);
                var dispatch = task?.DispatchHistory.FirstOrDefault(dispatch => IsBinding(dispatch, hold.BindingKey) && dispatch.DispatchedAt >= probe.Event.OccurredAt);
                return dispatch is null ? null : observations.FirstOrDefault(item => item.Task == task && item.Dispatch == dispatch).Verification;
            })
            .Where(verification => verification is not null && verification.CompletedAt > exhaustion.CompletedAt)
            .Select(verification => verification!).Distinct().ToArray();
        var attempt = k + completedProbes.Length;
        var reset = notes.Where(item => item.Event.OccurredAt > exhaustion.CompletedAt &&
                TryParseReset(item.Event, out var marker) && SameBinding(marker.BindingKey, hold.BindingKey))
            .OrderBy(item => item.Event.OccurredAt).LastOrDefault();
        DateTimeOffset? resetAt = null;
        var source = "backoff";
        if (reset.Event is not null && TryParseReset(reset.Event, out var resetMarker))
        {
            resetAt = resetMarker.ResetAt;
            source = "operator";
        }
        else if (TryGetResetAt(exhaustion, out var parsed))
        {
            resetAt = parsed;
            source = "parsed";
        }
        var latestCompletion = completedProbes.Select(verification => verification.CompletedAt).Append(exhaustion.CompletedAt).Max();
        var next = completedProbes.Length == 0 && resetAt is { } known ? known :
            latestCompletion.AddMinutes(Math.Min(30 * Math.Pow(2, Math.Min(attempt - 1, 4)), 360)).ToUniversalTime();
        var inFlight = latestProbe.Event is not null && dispatches.Any(item => item.Dispatch.DispatchedAt >= latestProbe.Event.OccurredAt &&
            !observations.Any(observation => observation.Task == item.Task && observation.Dispatch == item.Dispatch));
        GoalId? admittedGoal = null;
        TaskId? admittedTask = null;
        if (latestProbe.Event is { } probeEvent && TryParseProbe(probeEvent, out var probeMarker) && probeMarker.Due == next &&
            !dispatches.Any(item => item.Dispatch.DispatchedAt >= probeEvent.OccurredAt) &&
            !latestProbe.Goal.IsTerminal &&
            latestProbe.Goal.Tasks.Any(task => task.Id == probeEvent.TaskId && task.Status == WorkTaskStatus.Assigned && task.AssignedAgentId is not null) &&
            !latestProbe.Goal.Timeline.Skip(latestProbe.Index + 1).Any(evt => evt.TaskId == probeEvent.TaskId &&
                evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or ProgressKind.TaskCancelled))
        {
            admittedGoal = latestProbe.Goal.Id;
            admittedTask = probeEvent.TaskId;
        }
        var lastOutcome = "none";
        if (inFlight) lastOutcome = "running";
        else if (latestProbe.Event is { } latestEvent)
        {
            var latestDispatch = dispatches.Where(item => item.Task.Id == latestEvent.TaskId && item.Dispatch.DispatchedAt >= latestEvent.OccurredAt)
                .OrderBy(item => item.Dispatch.DispatchedAt).FirstOrDefault();
            var result = observations.FirstOrDefault(item => item.Task == latestDispatch.Task && item.Dispatch == latestDispatch.Dispatch).Verification;
            if (result is not null) lastOutcome = result.ProviderFailureKind == ProviderFailureKind.BudgetExhausted ? "exhausted" : "inconclusive";
        }
        return new(hold, attempt, resetAt, source, next, inFlight, admittedGoal, admittedTask, lastOutcome);
    }

    private static bool SameBinding(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool IsBinding(TaskDispatchRecord dispatch, string bindingKey) => SameBinding(
        DispatchFailureClassifier.BuildProviderBindingKey(dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory).Key, bindingKey);
}
