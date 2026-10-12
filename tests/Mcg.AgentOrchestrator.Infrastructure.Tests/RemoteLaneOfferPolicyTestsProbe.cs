using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneOfferPolicyTestsProbe
{
    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Probe_OffersPriorWithFewerThanThreeRemoteSamples(int samples)
    {
        var inputs = new RemoteLaneOfferInputs("lane", 150, 3, 999, null, samples, 300);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.True(decision.Offer);
        Assert.Equal("fits-probe", decision.Reason);
        Assert.Equal(315, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(142.5, 300, true, "fits")]
    [Xunit.InlineData(165, 345, true, "fits-probe")]
    [Xunit.InlineData(165.5, 346, false, "remote-slower")]
    public void Probe_BandHasExclusiveLowerAndInclusiveUpperBound(double local, double prior,
        bool offer, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", local, 3, 999, null, 0, 300);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.Equal(offer, decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(prior, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, 0, "remote-slower")]
    [Xunit.InlineData(3, 1, "remote-failing")]
    [Xunit.InlineData(5, 7, "remote-slower")]
    public void Probe_AnyRecordedFailureBlocksPrior(int failures, int ageHours, string reason)
    {
        var now = DateTimeOffset.Parse("2026-10-12T00:00:00Z");
        var inputs = new RemoteLaneOfferInputs("lane", 150, 3, 999, null, 0, 300,
            failures, now.AddHours(-ageHours), now);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.False(decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(315, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(45, 0, 105, "short")]
    [Xunit.InlineData(150, 3, 315, "remote-slower")]
    public void Probe_ShortLaneAndInvalidMeasuredMedianStayLocal(double local, int samples,
        double prior, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", local, 3, 999, null, samples, 300);

        var decision = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.False(decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(prior, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Fact]
    public void Probe_MeasuredLaneRetainsMeasuredRules()
    {
        var inputs = new RemoteLaneOfferInputs("lane", 150, 3, 999, 330, 3, 300);

        var withinSlack = RemoteLaneOfferPolicy.Decide(inputs);

        Assert.True(withinSlack.Offer);
        Assert.Equal("fits-slack", withinSlack.Reason);
        Assert.Equal(330, withinSlack.RemoteSeconds);
        Assert.Equal(inputs, withinSlack.Inputs);

        var slowerInputs = inputs with { RemoteMedianSeconds = 400 };
        var slower = RemoteLaneOfferPolicy.Decide(slowerInputs);

        Assert.False(slower.Offer);
        Assert.Equal("remote-slower", slower.Reason);
        Assert.Equal(400, slower.RemoteSeconds);
        Assert.Equal(slowerInputs, slower.Inputs);
    }
}
