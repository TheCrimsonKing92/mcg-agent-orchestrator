using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all inputs are explicit values, with no shared state or I/O.
public sealed class ConductorStartupProgressDeadlineTests
{
    private static readonly DateTimeOffset ReadyAt =
        new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMinutes(2);

    [Xunit.Fact]
    public void RecentLine_LeavesFullSilenceWindow()
    {
        var now = ReadyAt.AddSeconds(30);
        var result = ConductorStartupProgressDeadline.Compute(ReadyAt, now, now, SilenceWindow);

        Assert.Equal(SilenceWindow, result.Delay);
        Assert.False(result.CeilingReached);
    }

    [Xunit.Fact]
    public void RecentLineNearCeiling_ShortensDelayToRemainingCeiling()
    {
        var now = ReadyAt + ConductorStartupProgressDeadline.Ceiling - TimeSpan.FromMinutes(1);
        var result = ConductorStartupProgressDeadline.Compute(ReadyAt, now, now, SilenceWindow);

        Assert.Equal(TimeSpan.FromMinutes(1), result.Delay);
        Assert.False(result.CeilingReached);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void RecentLineAtOrBeyondCeiling_ReportsCeilingReached(int secondsBeyond)
    {
        var now = ReadyAt + ConductorStartupProgressDeadline.Ceiling + TimeSpan.FromSeconds(secondsBeyond);
        var result = ConductorStartupProgressDeadline.Compute(
            ReadyAt, now.AddSeconds(-1), now, SilenceWindow);

        Assert.True(result.CeilingReached);
        Assert.Equal(TimeSpan.Zero, result.Delay);
    }

    [Xunit.Fact]
    public void StaleLine_AnchorsSilenceWindowAtLoopReady()
    {
        var result = ConductorStartupProgressDeadline.Compute(
            ReadyAt, ReadyAt.AddMinutes(-1), ReadyAt, SilenceWindow);

        Assert.Equal(SilenceWindow, result.Delay);
        Assert.False(result.CeilingReached);
    }

    [Xunit.Fact]
    public void ElapsedSilenceWindow_ReportsZeroDelayWithoutCeilingReached()
    {
        var result = ConductorStartupProgressDeadline.Compute(
            ReadyAt, ReadyAt.AddSeconds(30), ReadyAt.AddSeconds(45), SilenceWindow);

        Assert.Equal(SilenceWindow - TimeSpan.FromSeconds(15), result.Delay);
        Assert.False(result.CeilingReached);

        result = ConductorStartupProgressDeadline.Compute(
            ReadyAt, ReadyAt, ReadyAt + SilenceWindow, SilenceWindow);
        Assert.Equal(TimeSpan.Zero, result.Delay);
        Assert.False(result.CeilingReached);
    }
}
