namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleStopClassifier
{
    internal static (string Reason, DateTimeOffset At)? Classify(IReadOnlyList<OwnerConductEvent> events)
    {
        // Stable ordering also preserves arrival order for events sharing a timestamp.
        var ordered = events.OrderBy(item => item.Timestamp).ToArray();
        var index = Array.FindLastIndex(ordered, item => item.EventKind is "loop-stop" or "loop-start");
        if (index < 0 || ordered[index].EventKind != "loop-stop") return null;

        var stop = ordered[index];
        var reason = OwnerActivityNarrator.Field(stop, "reason");
        if (reason is not ("self-relaunch-handoff" or "stop-file" or "stop-after-operator-intent" or
            "stop-while-idle" or "stop-file-during-sleep")) return null;

        if (ordered.Skip(index + 1).Any(item => item.EventKind == "loop-handoff" &&
            item.Detail.Split(' ', 2)[0] is "ACTIVATION_FAILED_BOTH" or "LOOP_HANDOFF_FAILED")) return null;

        return (reason, stop.Timestamp);
    }
}
