using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneOfferPolicyTests
{
    [Xunit.Theory]
    [Xunit.InlineData(345, 3, false, "remote-failing")]
    [Xunit.InlineData(345, 2, true, "fits")]
    [Xunit.InlineData(45, 3, false, "short")]
    public void RecentFailures_ThresholdHoldsLaneAfterShortRule(double local, int count,
        bool offer, string reason)
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var baseline = new RemoteLaneOfferInputs("lane", local, 3, 999, 300, 3, 400);
        var decision = RemoteLaneOfferPolicy.Decide(baseline with
        {
            ConsecutiveRemoteFailures = count, NewestRemoteFailureAt = now.AddHours(-1), DecisionTime = now
        });
        Assert.Equal(offer, decision.Offer);
        Assert.Equal(reason, decision.Reason);
        if (count == 2) Assert.Equal(RemoteLaneOfferPolicy.Decide(baseline).Reason, decision.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(6)]
    [Xunit.InlineData(7)]
    public void OldFailures_AtHoldBoundaryRestoreBaselineDecision(int ageHours)
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var baseline = new RemoteLaneOfferInputs("lane", 345, 3, 999, 300, 3, 400);
        var expected = RemoteLaneOfferPolicy.Decide(baseline);
        var recent = baseline with
        {
            ConsecutiveRemoteFailures = 5, NewestRemoteFailureAt = now.AddHours(-1), DecisionTime = now
        };
        Assert.False(RemoteLaneOfferPolicy.Decide(recent).Offer);
        Assert.Equal("remote-failing", RemoteLaneOfferPolicy.Decide(recent).Reason);
        var decision = RemoteLaneOfferPolicy.Decide(recent with { NewestRemoteFailureAt = now.AddHours(-ageHours) });
        Assert.True(decision.Offer);
        Assert.Equal(expected.Offer, decision.Offer);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(expected.LocalSeconds, decision.LocalSeconds);
        Assert.Equal(expected.RemoteSeconds, decision.RemoteSeconds);
    }

    [Xunit.Fact]
    public void RecentFailures_TakePrecedenceOverRemoteSlower()
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var decision = RemoteLaneOfferPolicy.Decide(new("lane", 120, 3, 999, null, 0, 120,
            3, now.AddHours(-1), now));
        Assert.False(decision.Offer);
        Assert.Equal("remote-failing", decision.Reason);
        Assert.Equal(" Fn=3 Fage=1.0", RemoteLaneOfferPolicy.FailureFields(decision));
    }

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
