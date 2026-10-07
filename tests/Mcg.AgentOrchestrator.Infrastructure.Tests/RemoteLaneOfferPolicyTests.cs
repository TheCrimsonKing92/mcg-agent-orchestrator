using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneOfferPolicyTests
{
    [Xunit.Theory]
    [Xunit.InlineData(45, 30, 400, false, "short")]
    [Xunit.InlineData(243, 606, 364, false, "remote-slower")]
    [Xunit.InlineData(345, 300, 400, true, "fits")]
    [Xunit.InlineData(60, 400, 400, true, "fits")]
    public void Decide_UsesOrderedRulesAndEchoesInputs(double local, double remote, double finish,
        bool offer, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", local, 3, 999, remote, 3, finish);
        var decision = RemoteLaneOfferPolicy.Decide(inputs);
        Assert.Equal(offer, decision.Offer);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(local, decision.LocalSeconds);
        Assert.Equal(remote, decision.RemoteSeconds);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(400, false)]
    [Xunit.InlineData(420, true)]
    public void TooFewRemoteSamples_UsesPrior(double finish, bool offer)
    {
        var inputs = new RemoteLaneOfferInputs("lane", 200, 3, 20, 1, 2, finish);
        var decision = RemoteLaneOfferPolicy.Decide(inputs);
        Assert.Equal(415, decision.RemoteSeconds);
        Assert.Equal(offer, decision.Offer);
        Assert.Equal(offer ? "fits" : "remote-slower", decision.Reason);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(2, 45, "short")]
    [Xunit.InlineData(3, 500, "fits")]
    public void LocalEstimate_RequiresThreeSamples(int samples, double expected, string reason)
    {
        var inputs = new RemoteLaneOfferInputs("lane", 500, samples, 45, 100, 3, 500);
        var decision = RemoteLaneOfferPolicy.Decide(inputs);
        Assert.Equal(expected, decision.LocalSeconds);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(inputs, decision.Inputs);
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, 545)]
    [Xunit.InlineData(2, 300)]
    [Xunit.InlineData(3, 300)]
    public void Finish_IsLargerOfLongestLaneAndAverageLoad(int concurrency, double expected) =>
        Assert.Equal(expected, RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds([45, 300, 200], concurrency));
}
