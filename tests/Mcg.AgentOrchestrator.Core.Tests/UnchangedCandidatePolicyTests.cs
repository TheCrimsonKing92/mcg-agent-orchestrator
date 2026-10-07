using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class UnchangedCandidatePolicyTests
{
    [Xunit.Fact]
    public void PriorVerdictHoldsWithOriginalReasonAndOffset()
    {
        var reason = HoldReason();
        var facts = UnchangedCandidateFacts.From(new GoalId("goal-123"), reason);

        var decision = UnchangedCandidatePolicy.Evaluate(facts);

        Assert.Equal("goal-123", facts.GoalId);
        Assert.Equal("Reviewer", facts.Role);
        Assert.Equal("task-1", facts.PriorVerdictTaskId);
        Assert.Equal("2026-10-07T09:15:30.0000000+05:30", facts.PriorVerdictAt);
        Assert.Equal("passed", facts.PriorVerdict);
        Assert.Equal(reason.CandidateIdentity.Canonical, facts.CandidateIdentity);
        Assert.Equal(UnchangedCandidateAction.Hold, decision.Action);
        Assert.Equal(1, decision.DiscriminatingRung);
        Assert.Equal("unchanged-candidate", decision.DiscriminatingEvidence);
        Assert.Equal(reason.Render(), decision.Reason);

        var record = decision.ToRecord();
        Assert.Equal("unchanged-candidate", record.Stage);
        Assert.Equal("Hold", record.Action);
        Assert.Equal(1, record.Rung);
        Assert.Equal("unchanged-candidate", record.DiscriminatingEvidence);
        Assert.Equal(reason.Render(), record.Reason);
    }

    [Xunit.Fact]
    public void EmptyPriorVerdictProceeds()
    {
        var decision = UnchangedCandidatePolicy.Evaluate(new UnchangedCandidateFacts("goal-123"));

        Assert.Equal(UnchangedCandidateAction.Proceed, decision.Action);
        Assert.Equal(0, decision.DiscriminatingRung);
        Assert.Equal("proceed", decision.DiscriminatingEvidence);
    }

    [Xunit.Fact]
    public void RecordedFactsHaveFixedNamesAndEmptyDefaults()
    {
        var facts = new UnchangedCandidateFacts("goal-123").ToRecordedFacts();

        Assert.Equal(new[]
        {
            "goalId", "role", "priorVerdictTaskId", "priorVerdictAt", "priorVerdict", "candidateIdentity"
        }, facts.Select(fact => fact.Name));
        Assert.Equal("goal-123", facts[0].Value);
        Assert.All(facts.Skip(1), fact => Assert.Equal("", fact.Value));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RecordedFactsReplayTheSameDecision(bool hasVerdict)
    {
        var facts = hasVerdict
            ? UnchangedCandidateFacts.From(new GoalId("goal-123"), HoldReason())
            : new UnchangedCandidateFacts("goal-123");
        var original = UnchangedCandidatePolicy.Evaluate(facts).ToRecord();

        var restored = UnchangedCandidateFacts.FromRecordedFacts(original.Facts);
        var replay = UnchangedCandidatePolicy.Evaluate(restored).ToRecord();

        Assert.Equal(facts, restored);
        Assert.Equal(original.Stage, replay.Stage);
        Assert.Equal(original.Action, replay.Action);
        Assert.Equal(original.Rung, replay.Rung);
        Assert.Equal(original.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(original.Reason, replay.Reason);
        Assert.Equal(original.Facts.ToArray(), replay.Facts.ToArray());
        Assert.Equal(facts, UnchangedCandidateFacts.FromRecordedFacts(original.Facts.Reverse().ToArray()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("misspelled")]
    [Xunit.InlineData("extra")]
    [Xunit.InlineData("null-value")]
    public void InvalidRecordedFactsFailLoudly(string invalidShape)
    {
        var facts = UnchangedCandidateFacts.From(new GoalId("goal-123"), HoldReason()).ToRecordedFacts();
        var invalid = invalidShape switch
        {
            "missing" => facts.Take(5).ToArray(),
            "duplicate" => facts.Select(fact => fact.Name == "goalId" ? fact with { Name = "role" } : fact).ToArray(),
            "misspelled" => facts.Select(fact => fact.Name == "priorVerdictAt" ? fact with { Name = "priorVerdictAT" } : fact).ToArray(),
            "extra" => facts.Append(new PolicyDecisionFact("extra", "")).ToArray(),
            "null-value" => facts.Select(fact => fact.Name == "role" ? fact with { Value = null! } : fact).ToArray(),
            _ => throw new InvalidOperationException($"Unknown invalid shape '{invalidShape}'.")
        };

        Assert.Throws<InvalidOperationException>(() => UnchangedCandidateFacts.FromRecordedFacts(invalid));
    }

    private static UnchangedCandidateHoldReason HoldReason() => new(
        AgentRole.Reviewer, new TaskId("task-1"),
        new DateTimeOffset(2026, 10, 7, 9, 15, 30, TimeSpan.FromHours(5.5)),
        "passed", new CandidateIdentity("patch", "base", "manifest"));
}
