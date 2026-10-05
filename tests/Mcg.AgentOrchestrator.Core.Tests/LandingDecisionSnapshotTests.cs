using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public sealed class LandingDecisionSnapshotTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void CompletionDecisionReplaysFromRestoredFactsAlone(int rung)
    {
        var original = LandingCompletionPolicy.Evaluate(LandingCompletionPolicyTests.FactsForRung(rung));
        var expected = original.ToRecord();
        var outcome = original.Action == LandingCompletionAction.Hold ? "Held" : "Escalated";
        var payload = RoundTrip(new(outcome, "Verified", original.EscalationKind?.ToString(), expected));
        Assert.Equal(outcome, payload.OutcomeKind);
        Assert.Equal("Verified", payload.LifecycleState);
        Assert.Equal(original.EscalationKind?.ToString(), payload.EscalationKind);
        var recorded = AssertRecord(expected, payload);
        var restoredFacts = LandingCompletionFacts.FromRecordedFacts(recorded.Facts);
        Assert.Equal(original.Facts.EvidenceHoldState, restoredFacts.EvidenceHoldState);
        var replay = LandingCompletionPolicy.Evaluate(restoredFacts);
        Assert.Equal(original.Action, replay.Action);
        Assert.Equal(original.DiscriminatingRung, replay.DiscriminatingRung);
        Assert.Equal(original.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(original.Reason, replay.Reason);
        Assert.Equal(original.StableIdentity, replay.StableIdentity);
        Assert.Equal(original.EscalationKind, replay.EscalationKind);
    }

    [Theory]
    [InlineData("pre-landing", 0)]
    [InlineData("pre-landing", 1)]
    [InlineData("pre-landing", 2)]
    [InlineData("pre-landing", 3)]
    [InlineData("pre-merge", 0)]
    [InlineData("pre-merge", 1)]
    [InlineData("pre-merge", 2)]
    [InlineData("pre-merge", 3)]
    public void RebaseDecisionReplaysFromRestoredFactsAlone(string phase, int rung)
    {
        var original = LandingRebasePolicy.Evaluate(LandingRebasePolicyTests.FactsForRung(phase, rung));
        var expected = original.ToRecord();
        var outcome = rung == 1 ? "Done" : rung == 0 ? "Executed" : "Escalated";
        var state = rung == 1 ? "CleanedUp" : "Verified";
        var payload = RoundTrip(new(outcome, state, null, expected));
        Assert.Equal(outcome, payload.OutcomeKind);
        Assert.Equal(state, payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var recorded = AssertRecord(expected, payload);
        var replay = LandingRebasePolicy.Evaluate(LandingRebaseFacts.FromRecordedFacts(recorded.Facts));
        Assert.Equal(original.Action, replay.Action);
        Assert.Equal(original.DiscriminatingRung, replay.DiscriminatingRung);
        Assert.Equal(original.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(original.Reason, replay.Reason);
    }

    [Fact]
    public void LegacyDoneTickWithoutDecisionRestoresUnchanged()
    {
        var restored = RoundTrip(new("Done", "CleanedUp", null), omitDecision: true);
        Assert.Equal("Done", restored.OutcomeKind);
        Assert.Equal("CleanedUp", restored.LifecycleState);
        Assert.Null(restored.EscalationKind);
        Assert.Null(restored.Decision);
    }

    private static ConductorTickOutcomePayload RoundTrip(ConductorTickOutcomePayload payload, bool omitDecision = false)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist landing decision evidence.");
        kernel.RecordGoalPolicyDecision(goal.Id, "Landing evidence receipt.", payload);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var json = JsonNode.Parse(JsonSerializer.Serialize(goal.ToSnapshot(), options))!;
        if (omitDecision)
        {
            var tick = Assert.Single(json["Timeline"]!.AsArray(), node => node?["TickOutcome"] is not null);
            Assert.True(tick!["TickOutcome"]!.AsObject().Remove("Decision"));
        }
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json.ToJsonString(), options)!;
        var restored = Goal.FromSnapshot(snapshot);
        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        Assert.Equal("Landing evidence receipt.", evt.Message);
        return Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
    }

    private static PolicyDecisionRecord AssertRecord(PolicyDecisionRecord expected, ConductorTickOutcomePayload payload)
    {
        var recorded = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(expected.Stage, recorded.Stage);
        Assert.Equal(expected.Action, recorded.Action);
        Assert.Equal(expected.Rung, recorded.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, recorded.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, recorded.Reason);
        Assert.Equal(expected.Facts.Count, recorded.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, recorded.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, recorded.Facts[index].Value);
        }
        return recorded;
    }
}
