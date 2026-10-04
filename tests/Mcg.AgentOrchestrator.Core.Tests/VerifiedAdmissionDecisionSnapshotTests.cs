using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class VerifiedAdmissionDecisionSnapshotTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void RestoredDecisionReplaysFromItsFactsAlone(int rung)
    {
        var original = VerifiedAdmissionPolicy.Evaluate(VerifiedAdmissionPolicyTests.FactsForRung(rung));
        var expected = original.ToRecord();
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist verified admission evidence.");
        var outcome = rung == 2 ? "Escalated" : "Held";
        var state = rung == 2 ? "AcceptanceFailed" : "Verified";
        var kind = rung == 2 ? "AcceptanceVerificationFailed" : null;
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload(outcome, state, kind, expected));
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);
        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal(outcome, payload.OutcomeKind);
        Assert.Equal(state, payload.LifecycleState);
        Assert.Equal(kind, payload.EscalationKind);
        var recorded = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal("verified-admission", recorded.Stage);
        Assert.Equal(expected.Action, recorded.Action);
        Assert.Equal(expected.Rung, recorded.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, recorded.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, recorded.Reason);
        Assert.Equal(20, recorded.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, recorded.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, recorded.Facts[index].Value);
        }
        var replay = VerifiedAdmissionPolicy.Evaluate(VerifiedAdmissionFacts.FromRecordedFacts(recorded.Facts));
        Assert.Equal(original.Action, replay.Action);
        Assert.Equal(original.DiscriminatingRung, replay.DiscriminatingRung);
        Assert.Equal(original.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(original.StableIdentity, replay.StableIdentity);
        Assert.Equal(original.Reason, replay.Reason);
    }
}
