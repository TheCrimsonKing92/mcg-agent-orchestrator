namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorLifecycleProjection
{
    internal static ConductorStatusSnapshot.SinceObservation Project(ConductorLockOwnership? owner,
        DateTimeOffset? latestStart, (DateTimeOffset At, string Reason)? latestStop,
        DateTimeOffset? latestReady, DateTimeOffset? lastEvent, DateTimeOffset bootTime)
    {
        if (owner?.AcquiredAt is not { } acquired || owner.ProcessStartedAt is not { } processStarted)
            return Baseline(owner, latestStart, latestStop, lastEvent, bootTime);
        if (latestStop is { } stop && stop.At >= acquired)
            return Baseline(owner, latestStart, latestStop, lastEvent, bootTime);
        if (latestStart is { } start && start >= processStarted)
            return new("running", start, null);
        return new("starting", acquired, latestReady >= acquired ? "loop-ready" : "lock-acquired",
            latestStop is { } previous ? new("stopped", previous.At, previous.Reason) : null);
    }

    private static ConductorStatusSnapshot.SinceObservation Baseline(ConductorLockOwnership? owner,
        DateTimeOffset? latestStart, (DateTimeOffset At, string Reason)? latestStop,
        DateTimeOffset? lastEvent, DateTimeOffset bootTime)
    {
        if (owner is null && lastEvent is { } last && bootTime > last)
            return new("host-restart", bootTime, null);
        if (latestStop is { } stop && (latestStart is null || stop.At > latestStart))
            return new("stopped", stop.At, stop.Reason);
        return latestStart is { } start ? new("running", start, null) : new("unavailable", null, null);
    }
}
