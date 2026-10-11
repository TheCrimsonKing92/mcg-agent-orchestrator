using Mcg.AgentOrchestrator.Infrastructure;

// Pure snapshot classification with a fixed clock; parallel-safe.
public sealed class GateLivenessVerdictTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Theory]
    [Xunit.InlineData("verification-check", 100)]
    [Xunit.InlineData("dotnet-build", 0)]
    public void TwentyMinutesOldAndStillMovingIsRunning(string phase, long outputBytes)
    {
        var snapshot = Snapshot() with
        {
            StartedAt = Now.AddMinutes(-20),
            Phase = phase,
            OutputBytes = outputBytes
        };

        var verdict = GateLivenessVerdict.Classify(Status(snapshot), Now);

        Xunit.Assert.Equal(GateLiveness.Running, verdict.Liveness);
        Xunit.Assert.Equal("RUNNING", verdict.Label);
        Xunit.Assert.Equal(1, verdict.SlotIndex);
        Xunit.Assert.Equal("WorkerDispatchTests", verdict.Lane);
        Xunit.Assert.Equal(phase, verdict.Phase);
        Xunit.Assert.Equal(TimeSpan.FromSeconds(15), verdict.SinceLastMovement);
        Xunit.Assert.Equal(outputBytes, verdict.OutputBytes);
    }

    [Xunit.Fact]
    public void FrozenAfterTwoMinutesIsStalled()
    {
        var lastProgress = Now - GateLivenessVerdict.SilenceBound - TimeSpan.FromMinutes(1);
        var snapshot = Snapshot() with
        {
            StartedAt = lastProgress.AddMinutes(-2),
            LastProgressAt = lastProgress
        };

        var verdict = GateLivenessVerdict.Classify(Status(snapshot), Now);

        Xunit.Assert.Equal(GateLiveness.Stalled, verdict.Liveness);
        Xunit.Assert.Equal("STALLED", verdict.Label);
        Xunit.Assert.Equal(GateLivenessVerdict.SilenceBound + TimeSpan.FromMinutes(1), verdict.SinceLastMovement);
    }

    [Xunit.Fact]
    public void RecentStartDoesNotOverrideSilentProgress()
    {
        var snapshot = Snapshot() with
        {
            StartedAt = Now.AddSeconds(-30),
            LastProgressAt = Now - GateLivenessVerdict.SilenceBound - TimeSpan.FromSeconds(1)
        };

        Xunit.Assert.Equal(GateLiveness.Stalled, GateLivenessVerdict.Classify(Status(snapshot), Now).Liveness);
    }

    [Xunit.Fact]
    public void StoppedWriterIsStalledEvenWithFreshProgress()
    {
        var snapshot = Snapshot() with
        {
            LastObservedAt = Now - GateLivenessVerdict.SilenceBound - TimeSpan.FromSeconds(1)
        };

        Xunit.Assert.Equal(GateLiveness.Stalled, GateLivenessVerdict.Classify(Status(snapshot), Now).Liveness);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("invalid")]
    public void UnavailableHeartbeatIsNone(string reason)
    {
        var status = new GateHeartbeatStatus(1, "heartbeat.json", false, reason, null, null, null);

        var verdict = GateLivenessVerdict.Classify(status, Now);

        Xunit.Assert.Equal(GateLiveness.None, verdict.Liveness);
        Xunit.Assert.Equal("NONE", verdict.Label);
        Xunit.Assert.Null(verdict.SinceLastMovement);
    }

    [Xunit.Fact]
    public void MissingSnapshotIsNoneEvenWhenMarkedAvailable()
    {
        var status = new GateHeartbeatStatus(1, "heartbeat.json", true, null, null, null, null);

        Xunit.Assert.Equal(GateLiveness.None, GateLivenessVerdict.Classify(status, Now).Liveness);
    }

    [Xunit.Theory]
    [Xunit.InlineData("completed")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("timed-out")]
    public void NonRunningHeartbeatIsNone(string state)
    {
        var snapshot = Snapshot() with { State = state };

        Xunit.Assert.Equal(GateLiveness.None, GateLivenessVerdict.Classify(Status(snapshot), Now).Liveness);
    }

    [Xunit.Fact]
    public void ExactlyAtSilenceBoundIsRunning()
    {
        var snapshot = Snapshot() with
        {
            State = "RUNNING",
            LastProgressAt = Now - GateLivenessVerdict.SilenceBound,
            LastObservedAt = Now - GateLivenessVerdict.SilenceBound
        };

        Xunit.Assert.Equal(GateLiveness.Running, GateLivenessVerdict.Classify(Status(snapshot), Now).Liveness);
    }

    [Xunit.Fact]
    public void FutureTimestampsClampToZeroAge()
    {
        var snapshot = Snapshot() with
        {
            LastProgressAt = Now.AddMinutes(1),
            LastObservedAt = Now.AddMinutes(1)
        };

        var verdict = GateLivenessVerdict.Classify(Status(snapshot), Now);

        Xunit.Assert.Equal(GateLiveness.Running, verdict.Liveness);
        Xunit.Assert.Equal(TimeSpan.Zero, verdict.SinceLastMovement);
    }

    private static GateHeartbeatSnapshot Snapshot() => new(
        "abcdef12", "verification-check", "WorkerDispatchTests", 1, 111, 222, "running",
        Now.AddMinutes(-1), Now.AddSeconds(-2), Now.AddSeconds(-15), 12, 3, 15);

    // Deliberately inconsistent cached ages ensure classification reads the mechanism timestamps.
    private static GateHeartbeatStatus Status(GateHeartbeatSnapshot snapshot) => new(
        1, "heartbeat.json", true, null, snapshot, TimeSpan.Zero, TimeSpan.Zero);
}
