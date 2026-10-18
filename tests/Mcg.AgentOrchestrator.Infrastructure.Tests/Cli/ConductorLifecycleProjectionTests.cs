using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: literal inputs and a pure projection, with no shared state or I/O.
public sealed class ConductorLifecycleProjectionTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessStart = new(2026, 10, 13, 16, 11, 40, TimeSpan.Zero);
    private static readonly DateTimeOffset Acquired = new(2026, 10, 13, 16, 11, 46, TimeSpan.Zero);
    private static readonly DateTimeOffset OldStop = new DateTimeOffset(2026, 10, 13, 15, 41, 15, TimeSpan.Zero).AddTicks(8870694);
    private static readonly ConductorLockOwnership Owner = new(39752, Acquired, ProcessStart);

    [Fact]
    public void Project_NoOwner_PreservesBaselineObservations()
    {
        Assert.Equal(new("stopped", OldStop, "stop-file"), Project(null, stop: OldStop));
        Assert.Equal(new("running", ProcessStart, null), Project(null, ProcessStart, OldStop));
        Assert.Equal(new("unavailable", null, null), Project(null));
        Assert.Equal(new("host-restart", Acquired, null),
            ConductorLifecycleProjection.Project(null, null, (OldStop, "stop-file"), null, OldStop, Acquired));
        Assert.Equal(new("unavailable", null, null),
            ConductorLifecycleProjection.Project(null, null, null, null, null, Acquired));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Project_UnknownInstants_PreservesOwnedBaseline(bool acquiredKnown, bool processKnown)
    {
        var owner = new ConductorLockOwnership(39752, acquiredKnown ? Acquired : null,
            processKnown ? ProcessStart : null);
        Assert.Equal(new("stopped", OldStop, "stop-file"), Project(owner, stop: OldStop));
        Assert.Equal(new("running", ProcessStart, null), Project(owner, ProcessStart, OldStop));
        Assert.Equal(new("unavailable", null, null), Project(owner));
        // A known pid continues to suppress the host-restart rule even with unknown instants.
        Assert.Equal(new("stopped", OldStop, "stop-file"),
            ConductorLifecycleProjection.Project(owner, null, (OldStop, "stop-file"), null, OldStop, Acquired));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Project_StopAtOrAfterAcquisition_PreservesBaseline(int seconds)
    {
        var stop = Acquired.AddSeconds(seconds);
        Assert.Equal(new("stopped", stop, "stop-file"), Project(Owner, ProcessStart, stop));
        Assert.Equal(new("running", stop.AddSeconds(1), null), Project(Owner, stop.AddSeconds(1), stop));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    public void Project_StartAtOrAfterProcessStart_ReportsRunning(int seconds)
    {
        var start = ProcessStart.AddSeconds(seconds);
        Assert.Equal(new("running", start, null), Project(Owner, start, OldStop));
    }

    [Theory]
    [InlineData(-1, "lock-acquired")]
    [InlineData(0, "loop-ready")]
    [InlineData(1, "loop-ready")]
    public void Project_PredecessorStart_ReportsStartingWithHistoricalStop(int readySeconds, string stage)
    {
        Assert.Equal(new("starting", Acquired, stage, new("stopped", OldStop, "stop-file")),
            Project(Owner, ProcessStart.AddSeconds(-1), OldStop, Acquired.AddSeconds(readySeconds)));
    }

    [Fact]
    public void Project_NoEvents_ReportsLockAcquiredWithoutPrevious()
    {
        Assert.Equal(new("starting", Acquired, "lock-acquired"), Project(Owner));
        Assert.Equal(new("starting", Acquired, "loop-ready"), Project(Owner, ready: Acquired));
    }

    [Fact]
    public void Project_ReacquiredLease_KeepsOwnStartDespiteEarlierStop()
    {
        var start = new DateTimeOffset(2026, 10, 13, 16, 12, 19, TimeSpan.Zero);
        var reacquired = new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero);
        Assert.Equal(new("running", start, null), Project(Owner with { AcquiredAt = reacquired }, start,
            new DateTimeOffset(2026, 10, 13, 16, 20, 0, TimeSpan.Zero)));
    }

    private static ConductorStatusSnapshot.SinceObservation Project(ConductorLockOwnership? owner,
        DateTimeOffset? start = null, DateTimeOffset? stop = null, DateTimeOffset? ready = null) =>
        ConductorLifecycleProjection.Project(owner, start, stop is { } at ? (at, "stop-file") : null,
            ready, ready ?? start ?? stop, Boot);
}
