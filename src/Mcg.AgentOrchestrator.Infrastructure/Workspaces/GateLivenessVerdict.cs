namespace Mcg.AgentOrchestrator.Infrastructure;

public enum GateLiveness
{
    None,
    Running,
    Stalled
}

public sealed record GateLivenessVerdict(
    GateLiveness Liveness,
    int SlotIndex,
    string? Lane,
    string? Phase,
    TimeSpan? SinceLastMovement,
    long? OutputBytes)
{
    public static readonly TimeSpan SilenceBound = TimeSpan.FromMinutes(10);

    public string Label => Liveness switch
    {
        GateLiveness.None => "NONE",
        GateLiveness.Running => "RUNNING",
        GateLiveness.Stalled => "STALLED",
        _ => throw new InvalidOperationException($"Unknown gate liveness: {Liveness}.")
    };

    public static GateLivenessVerdict Classify(GateHeartbeatStatus status, DateTimeOffset now)
    {
        var snapshot = status.Snapshot;
        var sinceLastMovement = snapshot is null ? (TimeSpan?)null : Age(now, snapshot.LastProgressAt);
        var liveness = GateLiveness.None;
        if (status.IsAvailable && snapshot is not null &&
            string.Equals(snapshot.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            liveness = sinceLastMovement > SilenceBound || Age(now, snapshot.LastObservedAt) > SilenceBound
                ? GateLiveness.Stalled
                : GateLiveness.Running;
        }

        return new GateLivenessVerdict(
            liveness, status.SlotIndex, snapshot?.CurrentTarget, snapshot?.Phase,
            sinceLastMovement, snapshot?.OutputBytes);
    }

    private static TimeSpan Age(DateTimeOffset now, DateTimeOffset timestamp) =>
        now > timestamp ? now - timestamp : TimeSpan.Zero;
}
