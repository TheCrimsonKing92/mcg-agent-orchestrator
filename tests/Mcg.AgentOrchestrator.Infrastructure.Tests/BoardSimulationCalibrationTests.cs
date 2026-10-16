using Mcg.AgentOrchestrator.App.Simulation;

public sealed class BoardSimulationCalibrationTests
{
    [Theory]
    [InlineData(30, "within-bar")]
    [InlineData(31, "outside-bar")]
    [InlineData(-30, "within-bar")]
    [InlineData(-31, "outside-bar")]
    public void Time_bar_is_inclusive_and_deltas_are_signed(int minutes, string verdict)
    {
        var trace = BoardSimulationFixture.F1();
        var replay = new BoardSimulationResult([], trace.Landings.Select(l => new SimulatedLanding(l.GoalId, l.LandedAt.AddMinutes(minutes))).ToArray(), 0);
        var report = BoardSimulationCalibration.Compare(trace, replay);
        Assert.Equal(verdict, report.Verdict);
        Assert.All(report.Matched, m => Assert.Equal(minutes, m.DeltaMinutes));
        Assert.Equal(Math.Abs(minutes), report.MaxAbsDeltaMinutes);
    }

    [Theory]
    [InlineData(1, "within-bar")]
    [InlineData(2, "outside-bar")]
    public void Count_bar_lists_unmatched_goals_without_matching_them(int missing, string verdict)
    {
        var trace = BoardSimulationFixture.F1();
        var replay = new BoardSimulationResult([], trace.Landings.Skip(missing).Select(l => new SimulatedLanding(l.GoalId, l.LandedAt)).ToArray(), 0);
        var report = BoardSimulationCalibration.Compare(trace, replay);
        Assert.Equal(verdict, report.Verdict);
        Assert.Equal(-missing, report.CountDelta);
        Assert.Equal(4 - missing, report.Matched.Count);
        Assert.Equal(trace.Landings.Take(missing).Select(l => l.GoalId), report.RecordedOnly);
        Assert.Empty(report.SimulatedOnly);
        var reverse = BoardSimulationCalibration.Compare(trace with { Landings = trace.Landings.Skip(missing).ToArray() },
            replay with { Landings = trace.Landings.Select(l => new SimulatedLanding(l.GoalId, l.LandedAt)).ToArray() });
        Assert.Equal(missing, reverse.CountDelta);
        Assert.Equal(report.RecordedOnly, reverse.SimulatedOnly);
        Assert.Empty(reverse.RecordedOnly);
    }

    [Fact]
    public void Median_absolute_delta_averages_both_middle_values()
    {
        var trace = BoardSimulationFixture.F1();
        double[] deltas = [-20, 2, -8, 4];
        var replay = new BoardSimulationResult([], trace.Landings.Select((l, i) =>
            new SimulatedLanding(l.GoalId, l.LandedAt.AddMinutes(deltas[i]))).ToArray(), 0);
        var report = BoardSimulationCalibration.Compare(trace, replay);
        Assert.Equal(6, report.MedianAbsDeltaMinutes);
        Assert.Equal(20, report.MaxAbsDeltaMinutes);
        Assert.Equal(4, report.Matched.Count);
    }
}
