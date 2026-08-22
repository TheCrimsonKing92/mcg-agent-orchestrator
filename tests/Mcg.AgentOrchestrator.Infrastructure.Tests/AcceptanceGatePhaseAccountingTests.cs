using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceGatePhaseAccountingTests
{
    [Fact]
    public void KnownUnbracketedIntervalsBecomeExplicitUnattributedTime()
    {
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var accountant = AcceptanceGatePhaseAccountant.Start(time, "goal", progress.Add);
        time.Advance(TimeSpan.FromSeconds(1));
        accountant.TransitionTo(AcceptanceGatePhaseNames.GatePlan);
        time.Advance(TimeSpan.FromSeconds(2));
        accountant.Pause();
        time.Advance(TimeSpan.FromSeconds(1));
        accountant.TransitionTo(AcceptanceGatePhaseNames.LaneExecution);
        time.Advance(TimeSpan.FromSeconds(3));
        accountant.RecordLaneExecution(TimeSpan.FromSeconds(3));
        accountant.TransitionTo(AcceptanceGatePhaseNames.Finalize);
        time.Advance(TimeSpan.FromSeconds(4));
        accountant.MarkCompleted(passed: true);

        accountant.Dispose();

        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(Assert.Single(progress).PhaseBreakdown);
        Assert.Equal(TimeSpan.FromSeconds(11), breakdown.TotalDuration);
        Assert.Equal(TimeSpan.FromSeconds(6), breakdown.AttributedPhaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(3), breakdown.LaneExecutionDuration);
        Assert.Equal(
            breakdown.LaneExecutionDuration,
            breakdown.Phases.Single(phase => phase.Name == AcceptanceGatePhaseNames.LaneExecution).Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), breakdown.UnattributedDuration);
        Assert.Equal(
            breakdown.TotalDuration,
            breakdown.AttributedPhaseDuration +
            breakdown.LaneExecutionDuration +
            breakdown.UnattributedDuration);
    }

    [Fact]
    public void OverlappingLaneReceiptProducesNegativeRemainderWithoutClamping()
    {
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var accountant = AcceptanceGatePhaseAccountant.Start(time, null, progress.Add);
        accountant.TransitionTo(AcceptanceGatePhaseNames.CheckExecution);
        time.Advance(TimeSpan.FromSeconds(5));
        accountant.RecordLaneExecution(TimeSpan.FromSeconds(3));
        accountant.MarkCompleted(passed: true);

        accountant.Dispose();

        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(Assert.Single(progress).PhaseBreakdown);
        Assert.Equal(TimeSpan.FromSeconds(-3), breakdown.UnattributedDuration);
        Assert.Equal(
            breakdown.TotalDuration,
            breakdown.AttributedPhaseDuration +
            breakdown.LaneExecutionDuration +
            breakdown.UnattributedDuration);
    }

    [Fact]
    public void NestedPhaseSuspendsAndResumesItsParent()
    {
        var time = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var accountant = AcceptanceGatePhaseAccountant.Start(time, null, progress.Add);
        accountant.TransitionTo(AcceptanceGatePhaseNames.CheckExecution);
        time.Advance(TimeSpan.FromSeconds(2));
        using (accountant.BeginPhase(AcceptanceGatePhaseNames.LaneExecution))
        {
            time.Advance(TimeSpan.FromSeconds(3));
        }
        accountant.RecordLaneExecution(TimeSpan.FromSeconds(3));
        time.Advance(TimeSpan.FromSeconds(4));
        accountant.MarkCompleted(passed: true);

        accountant.Dispose();

        var breakdown = Assert.IsType<AcceptanceGatePhaseBreakdown>(Assert.Single(progress).PhaseBreakdown);
        Assert.Equal(
            TimeSpan.FromSeconds(6),
            breakdown.Phases.Single(phase => phase.Name == AcceptanceGatePhaseNames.CheckExecution).Duration);
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            breakdown.Phases.Single(phase => phase.Name == AcceptanceGatePhaseNames.LaneExecution).Duration);
        Assert.Equal(TimeSpan.Zero, breakdown.UnattributedDuration);
    }

    [Fact]
    public void OutOfOrderNestedPhaseClosureFailsLoudly()
    {
        var accountant = AcceptanceGatePhaseAccountant.Start(
            new RecordingTimeProvider(),
            null,
            _ => { });
        var parent = accountant.BeginPhase(AcceptanceGatePhaseNames.CheckExecution);
        var child = accountant.BeginPhase(AcceptanceGatePhaseNames.LaneExecution);

        var exception = Assert.Throws<InvalidOperationException>(parent.Dispose);

        Assert.Contains(AcceptanceGatePhaseNames.CheckExecution, exception.Message, StringComparison.Ordinal);
        Assert.Contains(AcceptanceGatePhaseNames.LaneExecution, exception.Message, StringComparison.Ordinal);
        child.Dispose();
        parent.Dispose();
        accountant.Dispose();
    }
}
