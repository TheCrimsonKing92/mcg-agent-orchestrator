using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleVerifyingGate
{
    internal static string Stage(Goal goal, OwnerConsoleGateProgress gate, DateTimeOffset now)
    {
        var end = OwnerConsoleGateEnd.LatestFor(gate.Ended, goal.Id.Value);
        if (end is null) return "verifying (no live gate)";
        var minutes = Math.Max(0, Math.Floor((now - end.At).TotalMinutes)).ToString(CultureInfo.InvariantCulture);
        return $"verifying (no live gate; last gate ended {minutes}m ago: {end.Result})";
    }

    internal static string? Landing(OwnerGoalDetailSnapshot detail, TimeZoneInfo zone)
    {
        var goal = detail.Goal;
        if (goal.Status != GoalStatus.Verifying || goal.CurrentHold is not null || !detail.GateObserved) return null;
        var entered = goal.Timeline.LastOrDefault(item => item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains("entered Verifying", StringComparison.OrdinalIgnoreCase))?.OccurredAt;
        entered ??= goal.Timeline.LastOrDefault()?.OccurredAt;
        if (detail.Gate is { } live)
        {
            var started = live.StartedAt ?? entered;
            return "gate running" + (started is { } at ? " since " + Time(at, zone) : "");
        }
        var line = "verifying" + (entered is { } time ? " since " + Time(time, zone) : "") + ", no live gate";
        return detail.LastGateEnd is { } end ? line + $"; last gate ended {Time(end.At, zone)}: {end.Result}" : line;
    }

    private static string Time(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
