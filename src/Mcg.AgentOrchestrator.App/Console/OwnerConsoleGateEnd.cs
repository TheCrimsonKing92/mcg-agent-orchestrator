namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleGateEnd(string GoalPrefix, DateTimeOffset At, string Result)
{
    internal static IReadOnlyList<OwnerConsoleGateEnd> Read(IEnumerable<OwnerConductEvent> events)
    {
        var ends = new List<OwnerConsoleGateEnd>();
        foreach (var item in events)
        {
            if (!item.Detail.StartsWith("ACCEPTANCE ", StringComparison.Ordinal)) continue;
            string? goal = null, result = null;
            foreach (var field in item.Detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = field.IndexOf('=');
                if (separator < 0) continue;
                var key = field[..separator];
                if (key == "goal") goal ??= field[(separator + 1)..];
                if (key == "result") result ??= field[(separator + 1)..];
            }
            if (!string.IsNullOrEmpty(goal) && !string.IsNullOrEmpty(result) && result is not ("started" or "running"))
                ends.Add(new(goal, item.Timestamp, result));
        }
        return ends;
    }

    internal static OwnerConsoleGateEnd? LatestFor(IReadOnlyList<OwnerConsoleGateEnd>? ends, string goalId)
    {
        OwnerConsoleGateEnd? latest = null;
        if (ends is null) return null;
        foreach (var end in ends)
            if (goalId.StartsWith(end.GoalPrefix, StringComparison.OrdinalIgnoreCase) &&
                (latest is null || end.At >= latest.At)) latest = end;
        return latest;
    }
}
