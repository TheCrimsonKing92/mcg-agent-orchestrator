using Mcg.AgentOrchestrator.Core;

public sealed class PreReviewEvidenceHoldPolicyTests
{
    [Theory]
    [InlineData("writer-busy", 1, "artifact-writer-busy")]
    [InlineData("attempt-running", 2, "attempt-running")]
    [InlineData("did-not-run", 3, "attempt-did-not-run")]
    public void Evaluate_HoldCondition_PreservesReasonAndAttributesRung(
        string condition, int rung, string evidence)
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123")
        {
            Condition = condition,
            HoldReason = "Exact rendered hold reason.\r\nDetails remain unchanged."
        };

        var decision = PreReviewEvidenceHoldPolicy.Evaluate(facts);

        Assert.Equal(PreReviewEvidenceHoldAction.Hold, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(facts.HoldReason, decision.Reason);
        var record = decision.ToRecord();
        Assert.Equal("pre-review-evidence", record.Stage);
        Assert.Equal("Hold", record.Action);
        Assert.Equal(rung, record.Rung);
        Assert.Equal(evidence, record.DiscriminatingEvidence);
        Assert.Equal(facts.HoldReason, record.Reason);
    }

    [Fact]
    public void Evaluate_EmptyCondition_ProceedsAtRungZero()
    {
        var decision = PreReviewEvidenceHoldPolicy.Evaluate(new PreReviewEvidenceHoldFacts("goal-123"));

        Assert.Equal(PreReviewEvidenceHoldAction.Proceed, decision.Action);
        Assert.Equal(0, decision.DiscriminatingRung);
        Assert.Equal("proceed", decision.DiscriminatingEvidence);
        Assert.Equal("Proceed", decision.ToRecord().Action);
    }

    [Fact]
    public void RecordedFacts_DefaultInputs_HaveFixedOrderAndEmptyUnreadValues()
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123").ToRecordedFacts();

        Assert.Equal(new[] { "goalId", "condition", "attemptId", "attemptOutcome", "holdReason" },
            facts.Select(fact => fact.Name));
        Assert.Equal("goal-123", facts[0].Value);
        Assert.All(facts.Skip(1), fact => Assert.Equal("", fact.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("writer-busy")]
    [InlineData("attempt-running")]
    [InlineData("did-not-run")]
    public void RecordedFacts_EachCondition_ReplaysEqualDecision(string condition)
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123")
        {
            Condition = condition,
            AttemptId = "attempt-456",
            AttemptOutcome = "BlockedBuildSlot",
            HoldReason = "Exact rendered hold reason."
        };
        var expected = PreReviewEvidenceHoldPolicy.Evaluate(facts).ToRecord();

        var restored = PreReviewEvidenceHoldFacts.FromRecordedFacts(facts.ToRecordedFacts());
        Assert.Equal(facts, restored);
        var actual = PreReviewEvidenceHoldPolicy.Evaluate(restored).ToRecord();

        Assert.Equal(expected.Stage, actual.Stage);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.Rung, actual.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, actual.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.Facts.ToArray(), actual.Facts.ToArray());
        Assert.Equal(facts, PreReviewEvidenceHoldFacts.FromRecordedFacts(expected.Facts.Reverse().ToArray()));
    }

    [Fact]
    public void RecordedFacts_MissingDuplicateExtraOrUnknownName_Throws()
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123").ToRecordedFacts();

        Assert.Throws<InvalidOperationException>(() =>
            PreReviewEvidenceHoldFacts.FromRecordedFacts(facts.Skip(1).ToArray()));
        Assert.Throws<InvalidOperationException>(() =>
            PreReviewEvidenceHoldFacts.FromRecordedFacts(facts.Append(new("extra", "")).ToArray()));
        Assert.Throws<InvalidOperationException>(() => PreReviewEvidenceHoldFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "goalId" ? fact with { Name = "attemptId" } : fact).ToArray()));
        Assert.Throws<InvalidOperationException>(() => PreReviewEvidenceHoldFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "goalId" ? fact with { Name = "unknown" } : fact).ToArray()));
    }

    [Fact]
    public void RecordedFacts_UnknownCondition_Throws()
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123") { Condition = "unknown" }.ToRecordedFacts();

        Assert.Throws<InvalidOperationException>(() => PreReviewEvidenceHoldFacts.FromRecordedFacts(facts));
    }

    [Theory]
    [InlineData("goalId")]
    [InlineData("condition")]
    [InlineData("attemptId")]
    [InlineData("attemptOutcome")]
    [InlineData("holdReason")]
    public void RecordedFacts_NullValue_Throws(string name)
    {
        var facts = new PreReviewEvidenceHoldFacts("goal-123").ToRecordedFacts();

        Assert.Throws<InvalidOperationException>(() => PreReviewEvidenceHoldFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == name ? fact with { Value = null! } : fact).ToArray()));
    }
}
