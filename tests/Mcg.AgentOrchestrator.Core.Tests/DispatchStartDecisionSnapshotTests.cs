using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DispatchStartDecisionSnapshotTests
{
    public static IEnumerable<object[]> SnapshotCases() => DispatchStartPolicyTests.Cases()
        .Where(values => (DispatchStartAction)values[1] != DispatchStartAction.Proceed);

    [Xunit.Theory]
    [Xunit.MemberData(nameof(SnapshotCases))]
    public void Decision_RoundTripsEveryField_AndReplaysFromRestoredFacts(
        DispatchStartFacts facts, DispatchStartAction action, int rung, string evidence, string reason)
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist dispatch-start evidence.");
        var expected = DispatchStartPolicy.Evaluate(facts).ToRecord();
        var outcomeKind = action == DispatchStartAction.Escalate ? "Escalated" : "Held";
        var escalationKind = action == DispatchStartAction.Escalate ? "Unspecified" : null;
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload(outcomeKind, "WorkspaceReady", escalationKind, expected));

        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        Assert.Equal(reason, evt.Message);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal(outcomeKind, payload.OutcomeKind);
        Assert.Equal("WorkspaceReady", payload.LifecycleState);
        Assert.Equal(escalationKind, payload.EscalationKind);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal("dispatch-start", decision.Stage);
        Assert.Equal(action.ToString(), decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(expected.Facts.Count, decision.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, decision.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, decision.Facts[index].Value);
        }

        var replay = DispatchStartPolicy.Evaluate(DispatchStartFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(decision.Action, replay.Action.ToString());
        Assert.Equal(decision.Rung, replay.DiscriminatingRung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, replay.Reason);
    }

    [Xunit.Fact]
    public void LegacyEscalatedPayload_WithoutDecision_RestoresNull()
    {
        const string json = "{\"OutcomeKind\":\"Escalated\",\"LifecycleState\":\"WorkspaceReady\",\"EscalationKind\":\"Unspecified\"}";
        var payload = JsonSerializer.Deserialize<ConductorTickOutcomePayload>(json)!;
        Assert.Null(payload.Decision);
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist legacy escalation.");
        kernel.RecordGoalPolicyDecision(goal.Id, "Legacy escalation reason.", payload);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;

        var restored = Goal.FromSnapshot(snapshot);

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        var restoredPayload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal("Escalated", restoredPayload.OutcomeKind);
        Assert.Equal("WorkspaceReady", restoredPayload.LifecycleState);
        Assert.Equal("Unspecified", restoredPayload.EscalationKind);
        Assert.Null(restoredPayload.Decision);
        Assert.Equal("Legacy escalation reason.", evt.Message);
    }
}
