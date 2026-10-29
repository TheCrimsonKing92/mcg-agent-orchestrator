namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerStatusPresenter
{
    internal string Compose(OwnerConsoleStatus status, OwnerConsoleGateProgress? gate, string bellText,
        DateTimeOffset? refreshedAt, DateTimeOffset now, TimeZoneInfo localZone,
        TimeSpan refreshInterval, IReadOnlyCollection<string> working)
    {
        var gateSegment = gate is not null ? " | " + OwnerConsoleGateFormatter.StatusSegment(gate) : "";
        var text = $"conductor: {(status.ConductorRunning ? "running" : "stopped")}{gateSegment} | active: {status.ActiveGoals} | decisions: {status.LiveDecisions} | last event: {Age(status.LastEventAge)} | landed today: {status.LandedToday} | bell: {bellText}";
        if (status.HiddenQuestions > 0) text += $" | hidden: {status.HiddenQuestions}";
        if (refreshedAt is { } refreshed)
        {
            var age = now - refreshed;
            text += age.TotalSeconds > 3 * refreshInterval.TotalSeconds
                ? $" | stale {Math.Max(1, (int)age.TotalMinutes)}m"
                : $" | refreshed {TimeZoneInfo.ConvertTime(refreshed, localZone):HH:mm:ss}";
        }
        if (status.FailedToday > 0) text += $" | failed today: {status.FailedToday}";
        if (working.Count > 0) text += $" | working: {string.Join(", ", working)}";
        return text;
    }

    private static string Age(TimeSpan? span) => span is null ? "unknown" :
        span.Value.TotalMinutes < 1 ? $"{span.Value.TotalSeconds:0}s" : span.Value.TotalHours < 1 ? $"{span.Value.TotalMinutes:0}m" : $"{span.Value.TotalHours:0}h";
}
