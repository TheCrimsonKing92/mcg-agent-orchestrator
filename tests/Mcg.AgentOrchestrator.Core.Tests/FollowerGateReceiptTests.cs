using Mcg.AgentOrchestrator.Core;

public sealed class FollowerGateReceiptTests
{
    [Fact]
    public void IdentityIsStableAndNormalizesRevisionCase()
    {
        var binding = Binding();
        var identity = FollowerGateIdentity.Create(binding);
        Assert.Matches("^follower-v1-[0-9a-f]{64}$", identity);
        Assert.Equal(identity, FollowerGateIdentity.Create(Binding()));
        var uppercase = new FollowerGateReceipt(binding.LeaderGoalId,
            binding.LeaderCandidateRevision.ToUpperInvariant(), binding.LeaderCandidateTree.ToUpperInvariant(),
            binding.BaseMainRevision.ToUpperInvariant(), binding.FollowerGoalId,
            binding.FollowerBranchHead.ToUpperInvariant(), binding.FollowerTestedTree.ToUpperInvariant(), binding.FollowerPlanIdentity);
        Assert.Equal(identity, FollowerGateIdentity.Create(uppercase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void IdentityBindsEveryField(int changedField)
    {
        var binding = Binding();
        var fields = new[] { binding.LeaderGoalId.Value, binding.LeaderCandidateRevision,
            binding.LeaderCandidateTree, binding.BaseMainRevision, binding.FollowerGoalId.Value,
            binding.FollowerBranchHead, binding.FollowerTestedTree, binding.FollowerPlanIdentity };
        fields[changedField] = changedField is 0 or 4 or 7 ? "changed" : new string('f', 40);
        var changed = new FollowerGateReceipt(new(fields[0]), fields[1], fields[2], fields[3],
            new(fields[4]), fields[5], fields[6], fields[7]);
        Assert.NotEqual(FollowerGateIdentity.Create(binding), FollowerGateIdentity.Create(changed));
    }

    [Theory]
    [InlineData(FollowerGateRunOutcome.Passed, FollowerGateDisposition.LandFollower)]
    [InlineData(FollowerGateRunOutcome.Failed, FollowerGateDisposition.ChargeFollower)]
    public void ExactLandingUsesGateVerdict(FollowerGateRunOutcome outcome, FollowerGateDisposition disposition)
    {
        var binding = Binding();
        Assert.Equal(new FollowerGateDecision(disposition, null),
            FollowerGateBindingRule.Decide(Run(binding, outcome), ExactLanding(binding)));
    }

    [Fact]
    public void InvalidatedRunKeepsItsRecordedReason()
    {
        var binding = Binding();
        var run = Run(binding, FollowerGateRunOutcome.Invalidated) with { InvalidReason = FollowerGateInvalidReason.LeaderFailed };
        Assert.Equal(new FollowerGateDecision(FollowerGateDisposition.Discard, FollowerGateInvalidReason.LeaderFailed),
            FollowerGateBindingRule.Decide(run, ExactLanding(binding)));
    }

    [Fact]
    public void InfrastructureFailureIsDiscardedWithoutBindingReason()
    {
        var binding = Binding();
        Assert.Equal(new FollowerGateDecision(FollowerGateDisposition.Discard, null),
            FollowerGateBindingRule.Decide(Run(binding, FollowerGateRunOutcome.InfrastructureFailure),
                ExactLanding(binding) with { LandedFirstParent = new string('f', 40) }));
    }

    [Fact]
    public void FailedRunOnAnotherBaseIsDiscarded()
    {
        var binding = Binding();
        Assert.Equal(new FollowerGateDecision(FollowerGateDisposition.Discard, FollowerGateInvalidReason.BaseMoved),
            FollowerGateBindingRule.Decide(Run(binding, FollowerGateRunOutcome.Failed),
                ExactLanding(binding) with { LandedFirstParent = new string('f', 40) }));
    }

    private static FollowerGateReceipt Binding() => new(new("leader"), new string('a', 40),
        new string('b', 40), new string('c', 40), new("follower"), new string('d', 40), new string('e', 40), "plan");

    private static FollowerGateRunReceipt Run(FollowerGateReceipt binding, FollowerGateRunOutcome outcome) =>
        new("receipt", FollowerGateIdentity.Create(binding), binding, outcome, DateTimeOffset.UnixEpoch, [], null, [], null);

    private static FollowerGateLandingObservation ExactLanding(FollowerGateReceipt binding) =>
        new(FollowerLeaderOutcome.Landed, binding.BaseMainRevision, binding.LeaderCandidateTree,
            binding.FollowerBranchHead, binding.FollowerTestedTree, binding.FollowerPlanIdentity);
}
