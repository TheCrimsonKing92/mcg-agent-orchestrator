using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all samples and timestamps are values; no external resources.
public sealed class ActivationAdmissionHealthTrackerTests
{
    [Xunit.Fact]
    public void CensusFailures_AtPolicyLimit_TripAndLatch()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < ActivationAdmissionHealthPolicy.CensusFailureTicks; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen, Observe(tracker, tick, failed: true).State);
        var verdict = Observe(tracker, ActivationAdmissionHealthPolicy.CensusFailureTicks, failed: true);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionCensusFailing, verdict.Reason);
        Assert.Equal("censusFailedTicks=8 firstTick=1 lastTick=8", verdict.Detail);
        Assert.Same(verdict, Observe(tracker, 9));
    }

    [Xunit.Fact]
    public void SuccessfulCensus_BreaksFailureStreak_ResetsCount()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var belowLimit = ActivationAdmissionHealthPolicy.CensusFailureTicks - 1;
        for (var tick = 1; tick <= belowLimit; tick++) Observe(tracker, tick, failed: true);
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(tracker, belowLimit + 1).State);
        for (var offset = 1; offset <= belowLimit; offset++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                Observe(tracker, belowLimit + 1 + offset, failed: true).State);
    }

    [Xunit.Fact]
    public void OccupiedWithoutLiveGate_AtPolicyLimit_TripsWithLastSample()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                Observe(tracker, tick, waiting: 2, occupied: 13).State);
        var verdict = Observe(tracker, ActivationAdmissionHealthPolicy.OccupiedWithoutLiveGateTicks,
            waiting: 2, occupied: 13);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=12 waiting=2 occupied=13 live=0", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "AdmissionCensusFailing")]
    [Xunit.InlineData(false, "AdmissionStalled")]
    public void Streak_AtElapsedCap_TripsBelowTickLimit(bool failed, string reason)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var start = DateTimeOffset.UnixEpoch;
        ActivationAdmissionSample Sample(int tick) => new(tick, failed, 2, 13, 0);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen, tracker.Observe(Sample(41), start).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(Sample(42), start + ActivationAdmissionHealthPolicy.StreakElapsedCap - TimeSpan.FromTicks(1)).State);
        var verdict = tracker.Observe(Sample(43), start + ActivationAdmissionHealthPolicy.StreakElapsedCap);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(reason, verdict.Reason.ToString());
        Assert.EndsWith(" elapsedSeconds=300", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(3, 1, 1)]
    [Xunit.InlineData(0, 13, 0)]
    [Xunit.InlineData(3, 0, 0)]
    public void HealthyControls_ForFortyTicks_StayHealthy(int waiting, int occupied, int live)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 40; tick++)
            Assert.Equal(ActivationAdmissionHealthState.Healthy,
                Observe(tracker, tick, waiting: waiting, occupied: occupied, live: live).State);
    }

    [Xunit.Fact]
    public void IsolatedFailure_BetweenHealthySamples_DoesNotTrip()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(tracker, 1).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen, Observe(tracker, 2, failed: true).State);
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(tracker, 3).State);
    }

    [Xunit.Fact]
    public void SwitchingStreakKind_ResetsCountAndElapsedStart()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < ActivationAdmissionHealthPolicy.CensusFailureTicks; tick++)
            Observe(tracker, tick, failed: true);
        var now = DateTimeOffset.UnixEpoch + ActivationAdmissionHealthPolicy.StreakElapsedCap;
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(8, false, 2, 13, 0), now).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(9, true, 2, 13, 0), now + ActivationAdmissionHealthPolicy.StreakElapsedCap).State);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=unknown waiting=2 occupied=13 live=0")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=failed waiting=2 occupied=13")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=failed waiting=2 occupied=13 live=-1")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=bad census=failed waiting=2 occupied=13 live=0")]
    public void MalformedLine_IsIgnored(string line) => Assert.False(ActivationAdmissionSample.TryParse(line, out _));

    [Xunit.Fact]
    public void FormattedSample_WithProgressSuffix_RoundTrips()
    {
        var sample = new ActivationAdmissionSample(41, true, 2, 13, 0);
        Assert.True(ActivationAdmissionSample.TryParse(sample.Format() + " ts=2026-10-16T00:00:00Z", out var parsed));
        Assert.Equal(sample, parsed);
    }

    private static ActivationAdmissionHealthVerdict Observe(ActivationAdmissionHealthTracker tracker,
        int tick, bool failed = false, int waiting = 0, int occupied = 0, int live = 0) =>
        tracker.Observe(new(tick, failed, waiting, occupied, live), DateTimeOffset.UnixEpoch.AddSeconds(tick));
}
