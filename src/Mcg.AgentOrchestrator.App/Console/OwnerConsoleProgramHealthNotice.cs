namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleProgramHealthNotice
{
    internal static string? Current(IEnumerable<OwnerConductEvent> events)
    {
        var latest = events.Where(item => item.EventKind == "program-health").OrderBy(item => item.Timestamp).LastOrDefault();
        const string head = "PROGRAM_HEALTH_ALERT";
        if (latest is null || !latest.Detail.StartsWith(head, StringComparison.Ordinal) ||
            (latest.Detail.Length > head.Length && !char.IsWhiteSpace(latest.Detail[head.Length]))) return null;
        const string marker = " diagnosis=";
        var start = latest.Detail.IndexOf(marker, StringComparison.Ordinal);
        return "Program health: " + (start >= 0 ? latest.Detail[(start + marker.Length)..] : latest.Detail);
    }
}
