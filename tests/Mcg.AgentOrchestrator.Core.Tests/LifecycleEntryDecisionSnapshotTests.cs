using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class LifecycleEntryDecisionSnapshotTests
{
    [Xunit.Theory]
    [Xunit.InlineData("lifecycle-entry", "AwaitingClarification", "Escalated")]
    [Xunit.InlineData("lifecycle-entry", "WorkspaceReady", "Held")]
    [Xunit.InlineData("created", "Created", "Held")]
    public void Decision_RoundTripsEveryField_AndReplaysFromRestoredFacts(string stage, string state, string outcome)
    {
        var expected = stage == "created"
            ? CreatedStagePolicy.Evaluate(new CreatedStageFacts
            {
                LeaseUnavailableMessage = "GOAL_OPERATION_BLOCKED goal=abcdef12 operation=conductor:workspace-create reason=concurrent-acceptance-or-replacement"
            }).ToRecord()
            : LifecycleEntryPolicy.Evaluate(new LifecycleEntryFacts(Enum.Parse<GoalLifecycleState>(state))
            {
                SliceBatchParentHold = outcome == "Held" ? "Slice-batch parent abcdef12 owns child goals and does not execute worker tasks." : "",
                StateIsFailed = false,
                AwaitingClarificationReason = outcome == "Escalated" ? "injected clarification reason" : "",
                PolicyName = "Conservative"
            }).ToRecord();
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist lifecycle-entry and created evidence.");
        var escalationKind = outcome == "Escalated" ? "Unspecified" : null;
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload(outcome, state, escalationKind, expected));

        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        Assert.Equal(expected.Reason, evt.Message);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal(outcome, payload.OutcomeKind);
        Assert.Equal(state, payload.LifecycleState);
        Assert.Equal(escalationKind, payload.EscalationKind);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(stage, decision.Stage);
        Assert.Equal(expected.Action, decision.Action);
        Assert.Equal(expected.Rung, decision.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, decision.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(stage == "created" ? 2 : 7, decision.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, decision.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, decision.Facts[index].Value);
        }

        var replay = stage == "created"
            ? CreatedStagePolicy.Evaluate(CreatedStageFacts.FromRecordedFacts(decision.Facts)).ToRecord()
            : LifecycleEntryPolicy.Evaluate(LifecycleEntryFacts.FromRecordedFacts(decision.Facts)).ToRecord();
        Assert.Equal(decision.Stage, replay.Stage);
        Assert.Equal(decision.Action, replay.Action);
        Assert.Equal(decision.Rung, replay.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, replay.Reason);
    }
}
