using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DispatchAdmissionDecisionSnapshotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(4, 4, 9, 0, 4, 4, null, null)]
    [Xunit.InlineData(8, 9, 9, 1, 8, 9, null, null)]
    [Xunit.InlineData(9, 12, 9, 0, 9, 12, null, null)]
    [Xunit.InlineData(0, 4, 9, 0, 4, 4, false, "Slice-batch admission refused.")]
    public void Decision_RoundTripsEveryField_AndReplaysFromRestoredFacts(
        int running, int configuredCap, int admissionCapacity, int reservedGateSlots,
        int effectiveCap, int policyMaximum, bool? sliceAllowed, string? sliceReason)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist dispatch admission evidence.");
        var expected = DispatchAdmissionPolicy.Evaluate(new(
            running, configuredCap, admissionCapacity, reservedGateSlots, effectiveCap,
            policyMaximum, sliceAllowed, sliceReason)).ToRecord();
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload("Held", "WorkspaceReady", null, expected));

        // Serialize the actual snapshot so the restored list cannot be the original reference.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(expected.Stage, decision.Stage);
        Assert.Equal(expected.Action, decision.Action);
        Assert.Equal(expected.Rung, decision.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, decision.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(expected.Facts.Count, decision.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, decision.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, decision.Facts[index].Value);
        }

        var replay = DispatchAdmissionPolicy.Evaluate(DispatchAdmissionFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(decision.Action, replay.Action.ToString());
        Assert.Equal(decision.Rung, replay.DiscriminatingRung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, replay.Reason);
    }

    [Xunit.Fact]
    public void LegacyPayload_WithoutDecision_RestoresNull()
    {
        const string json = "{\"OutcomeKind\":\"Held\",\"LifecycleState\":\"WorkspaceReady\",\"EscalationKind\":null}";
        var payload = JsonSerializer.Deserialize<ConductorTickOutcomePayload>(json)!;
        Assert.Null(payload.Decision);
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist legacy hold.");
        kernel.RecordGoalPolicyDecision(goal.Id, "Legacy hold reason.", payload);

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        var restoredPayload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal("Held", restoredPayload.OutcomeKind);
        Assert.Equal("WorkspaceReady", restoredPayload.LifecycleState);
        Assert.Null(restoredPayload.EscalationKind);
        Assert.Null(restoredPayload.Decision);
    }
}
