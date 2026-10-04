using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class VerifyingStageDecisionSnapshotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(6)]
    [Xunit.InlineData(8)]
    [Xunit.InlineData(9)]
    public void Decision_RoundTripsEveryField_AndReplaysFromRestoredFacts(int rung)
    {
        var facts = rung switch
        {
            1 => new VerifyingStageFacts(GoalLifecycleState.Verifying, false, GoalStatus.Verifying),
            6 => new VerifyingStageFacts(GoalLifecycleState.Verifying, true, GoalStatus.Verifying,
                ParallelAcceptanceEnabled: true, CandidateBuilt: true, ReplacementLeaseAvailable: true,
                AdmissionAdmitted: false, AdmissionReason: "acceptance width 1 reached; retry on next conduct tick"),
            8 => new VerifyingStageFacts(GoalLifecycleState.Verified, false, GoalStatus.Verified,
                ParallelAcceptanceEnabled: true, CandidateBuilt: true, ReplacementLeaseAvailable: true,
                ArtifactWriterBusy: false, AttemptDecisionKind: "Running", AttemptId: "attempt-123",
                NoTickWaitOutcome: "deadline-elapsed"),
            9 => new VerifyingStageFacts(GoalLifecycleState.Verified, true, GoalStatus.Verifying,
                ParallelAcceptanceEnabled: true, CandidateBuilt: true, ReplacementLeaseAvailable: true,
                AdmissionAdmitted: true, AdmissionReason: "admitted", ArtifactWriterBusy: false,
                AttemptDecisionKind: "Started", AttemptId: "attempt-123"),
            _ => throw new InvalidOperationException("Unsupported test rung.")
        };
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist verifying evidence.");
        var expected = VerifyingStagePolicy.Evaluate(facts).ToRecord();
        Assert.Equal(rung, expected.Rung);
        kernel.RecordGoalPolicyDecision(goal.Id, expected.Reason,
            new ConductorTickOutcomePayload("Held", "Verifying", null, expected));

        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot(), options), options)!;
        var restored = Goal.FromSnapshot(snapshot);

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(ProgressKind.GoalPolicyDecision, evt.Kind);
        var payload = Assert.IsType<ConductorTickOutcomePayload>(evt.TickOutcome);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("Verifying", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal("verifying", decision.Stage);
        Assert.Equal(expected.Action, decision.Action);
        Assert.Equal(expected.Rung, decision.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, decision.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(14, decision.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
        {
            Assert.Equal(expected.Facts[index].Name, decision.Facts[index].Name);
            Assert.Equal(expected.Facts[index].Value, decision.Facts[index].Value);
        }

        var replay = VerifyingStagePolicy.Evaluate(VerifyingStageFacts.FromRecordedFacts(decision.Facts));
        Assert.Equal(decision.Action, replay.Action.ToString());
        Assert.Equal(decision.Rung, replay.DiscriminatingRung);
        Assert.Equal(decision.DiscriminatingEvidence, replay.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, replay.Reason);
    }
}
