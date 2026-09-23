public sealed class BreakawayJobProbeTests
{
    [Xunit.Fact]
    public void NotInJobIsPermitted()
    {
        var result = BreakawayJobProbe.DecideFromJobState(isInJob: false, limitFlags: 0);

        Assert.Equal(BreakawayVerdict.Permitted, result.Verdict);
        Assert.True(result.IsPermitted);
    }

    [Xunit.Fact]
    public void BreakawayOkJobIsPermitted()
    {
        var result = BreakawayJobProbe.DecideFromJobState(isInJob: true, limitFlags: 0x00000800);

        Assert.Equal(BreakawayVerdict.Permitted, result.Verdict);
        Assert.True(result.IsPermitted);
    }

    [Xunit.Fact]
    public void SilentBreakawayOkJobIsPermitted()
    {
        var result = BreakawayJobProbe.DecideFromJobState(isInJob: true, limitFlags: 0x00001000);

        Assert.Equal(BreakawayVerdict.Permitted, result.Verdict);
        Assert.True(result.IsPermitted);
    }

    [Xunit.Fact]
    public void BreakawayForbiddingJobIsForbidden()
    {
        var result = BreakawayJobProbe.DecideFromJobState(isInJob: true, limitFlags: 0);

        Assert.Equal(BreakawayVerdict.Forbidden, result.Verdict);
        Assert.False(result.IsPermitted);
    }

    [Xunit.Fact]
    public void NestedJobWhoseInnermostJobForbidsBreakawayIsForbidden()
    {
        var result = BreakawayJobProbe.DecideFromJobState(isInJob: true, limitFlags: 0x00002000);

        Assert.Equal(BreakawayVerdict.Forbidden, result.Verdict);
        Assert.False(result.IsPermitted);
    }

    [Xunit.Fact]
    public void CurrentProcessResultIsCached()
    {
        var first = BreakawayJobProbe.CanCreateBreakawayChild();
        var second = BreakawayJobProbe.CanCreateBreakawayChild();

        Assert.Same(first, second);
    }
}
