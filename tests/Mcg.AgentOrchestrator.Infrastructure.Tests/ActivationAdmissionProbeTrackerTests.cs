using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all time is passed as values.
public sealed class ActivationAdmissionProbeTrackerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static string Probe(string phase, string id = "a", int waiting = 2) =>
        new ActivationAdmissionProbeLine(1, phase, phase == "none" ? "none" : id, waiting).Format();

    [Xunit.Fact]
    public void OneAttempt_AllThreePhases_Satisfies()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Observe(Probe("admitted"), Start).State);
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Observe(Probe("terminal"), Start).State);
        Assert.Equal(ActivationAdmissionProbeState.Satisfied, tracker.Observe(Probe("released"), Start).State);
        Assert.Equal(ActivationAdmissionProbeState.Satisfied, tracker.Evaluate(Start.AddDays(1)).State);
    }

    [Xunit.Fact]
    public void EmptyQueue_ThirdHealthyTick_Skips()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("none", waiting: 0), Start);
        for (var tick = 1; tick <= 3; tick++)
            Assert.Equal(tick == 3 ? ActivationAdmissionProbeState.Skipped : ActivationAdmissionProbeState.Pending,
                tracker.Observe($"TICK_END tick={tick} activation=true", Start).State);
        Assert.Equal("no-verified-goal-waiting", tracker.Evaluate(Start).SkipReason);
    }

    [Xunit.Fact]
    public void WaitingContinuously_AdmissionDeadline_Trips()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("none"), Start);
        Assert.Equal(Start + AcceptanceAdmissionPathSet.AdmissionTimeout, tracker.NextDeadline);
        var verdict = tracker.Observe("other output", Start + AcceptanceAdmissionPathSet.AdmissionTimeout);
        Assert.Equal(ActivationAdmissionProbeState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.ProbeNotAdmitted, verdict.Reason);
    }

    [Xunit.Fact]
    public void AdmittedWithoutRelease_CompletionDeadline_TripsOnPoll()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("admitted"), Start);
        tracker.Observe(Probe("terminal"), Start.AddMinutes(1));
        Assert.Equal(Start + AcceptanceAdmissionPathSet.CompletionTimeout, tracker.NextDeadline);
        var verdict = tracker.Evaluate(Start + AcceptanceAdmissionPathSet.CompletionTimeout);
        Assert.Equal(ActivationAdmissionProbeState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.ProbeNotCompleted, verdict.Reason);
    }

    [Xunit.Fact]
    public void AdmissionBeforeDeadline_StopsWaitingClock()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("none"), Start);
        tracker.Observe(Probe("admitted"), Start.AddMinutes(5).AddSeconds(-1));
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Evaluate(Start.AddMinutes(6)).State);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ReleaseWithoutFullSequence_DoesNotSatisfy(bool admitted)
    {
        var tracker = new ActivationAdmissionProbeTracker();
        if (admitted) tracker.Observe(Probe("admitted"), Start);
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Observe(Probe("released"), Start).State);
    }

    [Xunit.Fact]
    public void DifferentAttempt_CannotBorrowTerminalPhase()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("admitted"), Start);
        tracker.Observe(Probe("terminal"), Start);
        tracker.Observe(Probe("admitted", "b"), Start);
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Observe(Probe("released", "b"), Start).State);
    }

    [Xunit.Fact]
    public void QueueClears_ResetsContinuousWait()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("none"), Start);
        tracker.Observe(Probe("none", waiting: 0), Start.AddMinutes(4));
        tracker.Observe(Probe("none"), Start.AddMinutes(5));
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Evaluate(Start.AddMinutes(9)).State);
    }

    [Xunit.Fact]
    public void AbandonedAttempt_NextAttemptGetsItsOwnClock()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        tracker.Observe(Probe("admitted"), Start);
        tracker.Observe(Probe("none"), Start.AddMinutes(59));
        tracker.Observe(Probe("admitted", "b"), Start.AddMinutes(59));
        Assert.Equal(ActivationAdmissionProbeState.Pending, tracker.Evaluate(Start.AddMinutes(61)).State);
    }

    [Xunit.Fact]
    public void OldBuildWithoutProbe_ThirdTick_SkipsWithDistinctReason()
    {
        var tracker = new ActivationAdmissionProbeTracker();
        for (var tick = 1; tick <= 3; tick++) tracker.Observe($"TICK_END tick={tick} activation=true", Start);
        Assert.Equal("probe-line-absent", tracker.Evaluate(Start).SkipReason);
    }

    [Xunit.Fact]
    public void StampedLine_ParsesAndMalformedLine_IsIgnored()
    {
        var line = Probe("admitted") + " ts=2026-10-16T00:00:00Z";
        Assert.True(ActivationAdmissionProbeLine.TryParse(line, out var sample));
        Assert.Equal("a", sample.Attempt);
        Assert.False(ActivationAdmissionProbeLine.TryParse(Probe("bogus"), out _));
        Assert.False(ActivationAdmissionProbeLine.TryParse("ACTIVATION_ADMISSION_PROBE tick=bad", out _));
    }
}
