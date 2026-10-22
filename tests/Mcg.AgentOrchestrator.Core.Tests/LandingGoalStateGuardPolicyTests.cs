using Mcg.AgentOrchestrator.Core;

// Pure immutable facts and replay parsing; parallel-safe, with no I/O or ambient state.
public sealed class LandingGoalStateGuardPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldDecisionReplaysOnlyItsObservedFacts(bool parked)
    {
        var facts = parked
            ? new LandingGoalStateGuardFacts(GoalStatus.Parked, null, null, true)
            : new LandingGoalStateGuardFacts(null, GoalStatus.Active, true, false);
        var decision = LandingGoalStateGuardPolicy.Evaluate(facts);
        Assert.Equal(LandingGoalStateGuardAction.Hold, decision.Action);
        Assert.Equal(parked ? 2 : 1, decision.DiscriminatingRung);
        Assert.Equal(parked ? "parked-landing-held" : "acceptance-retry-already-prepared", decision.DiscriminatingEvidence);
        Assert.Equal(parked ? "Goal is parked; landing remains held."
            : "Acceptance retry is already prepared; terminal replay admits no new work.", decision.Reason);
        var recorded = decision.ToRecord();
        Assert.Equal("landing-goal-state-guard", recorded.Stage);
        Assert.Equal("Hold", recorded.Action);
        var restored = LandingGoalStateGuardFacts.FromRecordedFacts(recorded.Facts.Reverse().ToArray());
        Assert.Equal(facts, restored);
        Assert.Equal(decision, LandingGoalStateGuardPolicy.Evaluate(restored));
    }

    [Theory]
    [InlineData(null, GoalStatus.Active, true, true)]
    [InlineData(null, GoalStatus.Active, false, false)]
    [InlineData(null, GoalStatus.Verified, true, false)]
    [InlineData(GoalStatus.Active, null, null, true)]
    [InlineData(GoalStatus.Parked, null, null, false)]
    [InlineData(null, null, null, null)]
    public void IncompleteHoldPredicatePermitsContinuation(
        GoalStatus? status, GoalStatus? lifecycle, bool? deferred, bool? passed)
    {
        var facts = new LandingGoalStateGuardFacts(status, lifecycle, deferred, passed);
        var restored = LandingGoalStateGuardFacts.FromRecordedFacts(facts.ToRecordedFacts());
        Assert.Equal(facts, restored);
        Assert.Equal(LandingGoalStateGuardAction.Proceed, LandingGoalStateGuardPolicy.Evaluate(restored).Action);
    }

    [Theory]
    [InlineData("goalStatus", "parked")]
    [InlineData("goalStatus", "3")]
    [InlineData("lifecycleStatus", "999")]
    [InlineData("lifecycleStatus", "Active ")]
    [InlineData("acceptancePassed", "TRUE")]
    [InlineData("acceptanceFailureDeferredForRetry", "0")]
    public void InvalidRecordedValueCannotReplay(string name, string value)
    {
        var recorded = new LandingGoalStateGuardFacts(null, null, null, null).ToRecordedFacts()
            .Select(f => f.Name == name ? new PolicyDecisionFact(name, value) : f).ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingGoalStateGuardFacts.FromRecordedFacts(recorded));
    }

    [Fact]
    public void MissingDuplicateUnknownAndNullFactsCannotReplay()
    {
        var recorded = new LandingGoalStateGuardFacts(null, GoalStatus.Active, true, false).ToRecordedFacts().ToArray();
        Assert.Throws<InvalidOperationException>(() => LandingGoalStateGuardFacts.FromRecordedFacts(recorded[..^1]));
        foreach (var replacement in new PolicyDecisionFact[]
        {
            recorded[0], new("unknown", ""), new("acceptancePassed", null!), new(null!, "false"), null!
        })
        {
            var malformed = recorded.ToArray();
            malformed[^1] = replacement;
            Assert.Throws<InvalidOperationException>(() => LandingGoalStateGuardFacts.FromRecordedFacts(malformed));
        }
    }
}
