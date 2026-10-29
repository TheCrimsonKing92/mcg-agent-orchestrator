using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleLandingBlockerText
{
    internal static string? Board(Goal goal, DateTimeOffset now, int? queuePosition = null)
    {
        if (goal.Status is not (GoalStatus.Verified or GoalStatus.Verifying) || goal.LandingBlocker is not { } blocker)
            return null;
        var label = OwnerConsoleHoldPhrase.For(blocker.Kind, queuePosition);
        return $"{label}, {Age(blocker.Since, now)}";
    }

    internal static string Age(DateTimeOffset since, DateTimeOffset now)
    {
        var minutes = (long)Math.Max(0, Math.Floor((now - since).TotalMinutes));
        return minutes < 60 ? FormattableString.Invariant($"{minutes}m") :
            FormattableString.Invariant($"{minutes / 60}h{minutes % 60}m");
    }

    internal static string? Detail(Goal goal, TimeZoneInfo zone)
    {
        if (goal.Status is not (GoalStatus.Verified or GoalStatus.Verifying) || goal.LandingBlocker is not { } blocker)
            return null;
        var since = TimeZoneInfo.ConvertTime(blocker.Since, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var owner = blocker.Kind.StartsWith("untyped/", StringComparison.Ordinal) ? "; owner: " + blocker.Kind[8..] : "";
        var next = blocker.NextActor == "unmapped" ? "not mapped" : blocker.NextActor + " " + blocker.NextVerb;
        return $"{blocker.Kind} since {since}{owner}; next: {next}; detail: {blocker.Detail.Replace('\r', ' ').Replace('\n', ' ')}";
    }
}
