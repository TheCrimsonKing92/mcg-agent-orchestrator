using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneOfferPolicyTestsReprobe
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T00:00:00Z");

    [Xunit.Theory]
    [Xunit.InlineData(0, 3, 6)]
    [Xunit.InlineData(0, 4, 7)]
    [Xunit.InlineData(2, 5, 24)]
    public void Reprobe_OffersPriorAfterFailureHold(int samples, int failures, double age)
    {
        var inputs = new RemoteLaneOfferInputs("lane", 150, 3, 999, null, samples, 300,
            failures, Now.AddHours(-age), Now);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.True(decision.Offer);
        Assert.Equal("fits-reprobe", decision.Reason);
        Assert.Equal(315, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(217.5, 450, true, "fits-reprobe")]
    [Xunit.InlineData(218, 451, false, "remote-slower")]
    public void Reprobe_BandHasInclusiveUpperBound(double local, double prior, bool offer, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", local, 3, 999, null, 0, 300,
            3, Now.AddHours(-6), Now);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.Equal(offer, decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(prior, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(150, 0, 3, 5.99, true, 315, "remote-failing")]
    [Xunit.InlineData(150, 0, 2, 24, true, 315, "remote-slower")]
    [Xunit.InlineData(150, 0, 3, 7, false, 315, "remote-slower")]
    [Xunit.InlineData(150, 3, 4, 7, true, 315, "remote-slower")]
    [Xunit.InlineData(45, 0, 3, 7, true, 105, "short")]
    public void Reprobe_IneligiblePriorStaysLocal(double local, int samples, int failures,
        double age, bool hasNewestFailure, double prior, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", local, 3, 999, null, samples, 300,
            failures, hasNewestFailure ? Now.AddHours(-age) : null, Now);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.False(decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(prior, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Fact]
    public void Reprobe_MeasuredLaneRetainsMeasuredSlackAfterFailureHold()
    {
        var inputs = new RemoteLaneOfferInputs("lane", 150, 3, 999, 330, 3, 300,
            4, Now.AddHours(-7), Now);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.True(decision.Offer);
        Assert.Equal("fits-slack", decision.Reason);
        Assert.Equal(330, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }
}
