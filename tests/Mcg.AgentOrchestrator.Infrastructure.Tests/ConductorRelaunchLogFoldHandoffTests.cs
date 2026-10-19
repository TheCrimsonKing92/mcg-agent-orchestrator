using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: immutable fold inputs and explicit timestamps only.
public sealed class ConductorRelaunchLogFoldHandoffTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private const string Handoff = "LOOP_HANDOFF attempt=18 stagedSourceCommit=d367dc3f8 repositoryHead=d367dc3f8 runDir=run";
    private const string Progress = "PHASE_PROGRESS goal=other phase=Tests elapsed_ms=30000 target=x child_pid=1 output_bytes=0 heartbeat=h";

    [Fact]
    public void Apply_OpenWindow_RecordsHandoffAndReplacesWithLaterAttempt()
    {
        Assert.Contains("LOOP_HANDOFF attempt=", ConductorRelaunchLogFold.Heads);
        Assert.Contains("PHASE_PROGRESS ", ConductorRelaunchLogFold.Heads);
        var state = Scheduled();
        state = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(1), Handoff);
        Assert.Equal(Start.AddMinutes(1), state.HandoffAt);
        state = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(2), Handoff.Replace("18", "19"));
        Assert.Equal(Start.AddMinutes(2), state.HandoffAt);
    }

    [Fact]
    public void Apply_NoWindowOrFailedWindow_IgnoresHandoffAndProgress()
    {
        var failed = ConductorRelaunchLogFold.Apply(Scheduled(), Start.AddMinutes(1), Handoff);
        failed = ConductorRelaunchLogFold.Apply(failed, Start.AddMinutes(2), "ACTIVATION_REVERTED reason=probe");
        foreach (var state in new[] { ConductorRelaunchObservation.Empty, failed })
        {
            Assert.Equal(state, ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(3), Handoff));
            Assert.Equal(state, ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(3), Progress));
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Apply_ProgressTimestamp_CountsOnlyAtOrAfterHandoff(int seconds, bool counts)
    {
        var handoff = Start.AddMinutes(1);
        var state = Scheduled();
        Assert.Equal(state, ConductorRelaunchLogFold.Apply(state, Start.AddSeconds(30), Progress));
        state = ConductorRelaunchLogFold.Apply(state, handoff, Handoff);
        var at = handoff.AddSeconds(seconds);
        var result = ConductorRelaunchLogFold.Apply(state, at, Progress);
        Assert.Equal(counts ? at : (DateTimeOffset?)null, result.LastLiveGateAt);
        Assert.Equal(handoff, result.HandoffAt);
    }

    [Fact]
    public void Apply_CohortProgress_RefreshesLivenessAcrossGoals()
    {
        var state = ConductorRelaunchLogFold.Apply(Scheduled(), Start.AddMinutes(1), Handoff);
        var at = Start.AddMinutes(2);
        var result = ConductorRelaunchLogFold.Apply(state, at,
            "PHASE_PROGRESS goal=a cohort=other member=a members=a,b phase=Tests elapsed_ms=30000 target=x child_pid=1 output_bytes=0 heartbeat=h");
        Assert.Equal(at, result.LastLiveGateAt);
    }

    [Fact]
    public void Apply_HandoffFailure_RecordsOutcomeWithoutRecordingHandoff()
    {
        var at = Start.AddMinutes(1);
        var result = ConductorRelaunchLogFold.Apply(Scheduled(), at,
            "LOOP_HANDOFF_FAILED attempt=18 phase=activation reason=probe");
        Assert.Null(result.HandoffAt);
        Assert.Equal(at, result.FailureOutcomeAt);
    }

    [Fact]
    public void Apply_OldLoopTick_DoesNotRecordHandoff()
    {
        var state = Scheduled();
        Assert.Equal(state, ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(1),
            "LOOP_HANDOFF tick=3 pid=1 reason=successor"));
    }

    [Fact]
    public void Apply_Adoption_ClearsWindowIncludingHandoffAndLiveness()
    {
        var state = Scheduled() with { RunningBuildCommit = "build" };
        state = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(1), Handoff);
        state = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(2), Progress);
        var result = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(3),
            "ACTIVATION_ADOPTED adoptedCommit=d367dc3f8");
        Assert.Equal(ConductorRelaunchObservation.Empty with { RunningBuildCommit = "build" }, result);
        Assert.Null(result.HandoffAt);
    }

    [Fact]
    public void Apply_NewScheduleAfterFailure_StartsWithoutOldHandoff()
    {
        var state = ConductorRelaunchLogFold.Apply(Scheduled(), Start.AddMinutes(1), Handoff);
        state = ConductorRelaunchLogFold.Apply(state, Start.AddMinutes(2), "ACTIVATION_REVERTED reason=probe");
        var at = Start.AddMinutes(3);
        state = ConductorRelaunchLogFold.Apply(state, at, "LOOP_RELAUNCH_SCHEDULED tick=2 goal=next");
        Assert.Equal(new(at, "next", null, null, null), state);
    }

    private static ConductorRelaunchObservation Scheduled() => ConductorRelaunchLogFold.Apply(
        ConductorRelaunchObservation.Empty, Start, "LOOP_RELAUNCH_SCHEDULED tick=1 goal=goal changedFiles=1 coalesced=true");
}
