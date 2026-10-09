using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Wording is a read-only projection of the current landing state, with history last.
internal static class OwnerConsoleGoalLanding
{
    internal static string? Line(Goal goal, int? queuePosition, TimeZoneInfo zone)
    {
        if (goal.Status == GoalStatus.Completed)
        {
            var landed = goal.MetadataTerminatedAt ?? goal.Timeline.LastOrDefault()?.OccurredAt;
            return landed is { } time ? "landed " + Time(time, zone) : null;
        }
        if (goal.CurrentHold is { } hold) return "held: " + OwnerActivityNarrator.WaitingOn(hold.Blocker);
        if (goal.Status == GoalStatus.Verifying)
        {
            var started = goal.Timeline.LastOrDefault(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains("entered Verifying", StringComparison.OrdinalIgnoreCase))?.OccurredAt;
            started ??= goal.Timeline.LastOrDefault()?.OccurredAt;
            return started is { } time ? "gate running since " + Time(time, zone) : null;
        }
        if (queuePosition is { } position) return $"waiting for the gate ({Ordinal(position)})";
        return goal.RetainedAcceptanceFailure is { } failure
            ? $"last gate failed {Time(failure.OccurredAt, zone)}: {failure.FailedChecks.FirstOrDefault() ?? "acceptance check"}"
            : null;
    }

    internal static string Ordinal(int position)
    {
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        var suffix = position % 100 is 11 or 12 or 13 ? "th" : (position % 10) switch
        { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return position.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }

    internal static string FailureText(AcceptanceFailureSummary failure, TimeZoneInfo zone) =>
        $"Last gate failed {Time(failure.OccurredAt, zone)}" + Environment.NewLine +
        string.Join(Environment.NewLine, failure.FailedChecks.Select(check => check + Environment.NewLine +
            string.Join(Environment.NewLine, (failure.CheckAttributions?.FirstOrDefault(item => item.CheckName == check)?.Evidence ?? "")
                .Replace("\r", "").Split('\n').Take(3))));

    private static string Time(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
