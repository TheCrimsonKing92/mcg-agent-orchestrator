using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parallel-safe: each test owns its kernel and snapshot.
public sealed class LandingPolicyDecisionSnapshotTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RestoredDecision_ReplaysFromRecordedFactsAlone(int rung)
    {
        var original = LandingPolicy.Evaluate(LandingPolicyTests.FactsForRung(rung));
        var expected = original.ToRecord();
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist landing policy evidence.");
        var outcome = rung == 0 ? "Executed" : "Escalated";
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload(outcome, "Verified", null, expected));
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);
        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal(outcome, payload.OutcomeKind);
        Assert.Equal("Verified", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var recorded = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal("landing", recorded.Stage);
        Assert.Equal(expected.Action, recorded.Action);
        Assert.Equal(expected.Rung, recorded.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, recorded.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, recorded.Reason);
        Assert.Equal(10, recorded.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, recorded.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, recorded.Facts[index].Value);
        }
        var replay = LandingPolicy.Evaluate(LandingFacts.FromRecordedFacts(recorded.Facts));
        Assert.Equal(original.Action, replay.Action);
        Assert.Equal(original.DiscriminatingRung, replay.DiscriminatingRung);
        Assert.Equal(original.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(original.Reason, replay.Reason);
    }
}
