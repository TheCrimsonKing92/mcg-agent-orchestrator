using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: producer decisions use only test-owned in-memory inputs.
public sealed class HeldOwnerProducerTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void FocusedEvidence_RunningDecision_AssignsBackgroundAttempt()
    {
        const string reason = "Background deferred-no-change focused evidence is running in attempt abc.";
        var observation = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingEvidencePending, reason);

        var held = ConductorDriver.FocusedEvidencePendingHeld(
            GoalLifecycleState.WorkspaceReady, observation,
            ConductorParallelAcceptanceAttemptDecisionKind.Running);

        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Equal(reason, held.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData((int)ConductorParallelAcceptanceAttemptDecisionKind.Started)]
    [Xunit.InlineData((int)ConductorParallelAcceptanceAttemptDecisionKind.Completed)]
    [Xunit.InlineData((int)ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)]
    [Xunit.InlineData(null)]
    public void FocusedEvidence_WithoutRunningDecision_RetainsNoOwner(
        int? kind)
    {
        var observation = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingEvidencePending, "pending evidence");
        var decisionKind = kind.HasValue
            ? (ConductorParallelAcceptanceAttemptDecisionKind?)kind.Value
            : null;

        var held = ConductorDriver.FocusedEvidencePendingHeld(
            GoalLifecycleState.WorkspaceReady, observation, decisionKind);

        Assert.Equal(ConductorHoldOwner.None, held.Owner);
    }

    [Xunit.Fact]
    public void AcceptanceAdmission_LiveWidthFilled_AssignsAcceptanceQueue()
    {
        var (_, goal) = SimpleGoal();
        var census = new ConductorBatchLoop.LiveAcceptanceCensus(["goal:abcd1234"]);
        var decision = ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width: 1);
        Assert.False(decision.IsAdmitted);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            ConductorBatchLoop.AdmissionDeniedHeld(
                goal, ConductorAutonomyPolicy.Conservative, decision).Outcome);

        Assert.Equal(ConductorHoldOwner.AcceptanceQueue, held.Owner);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(
            "acceptance width 1 reached; live acceptance occupants goal:abcd1234; retry on next conduct tick",
            held.Reason);
    }

    [Xunit.Fact]
    public void AcceptanceAdmission_CensusUnavailable_RetainsNoOwner()
    {
        var (_, goal) = SimpleGoal();
        var census = new ConductorBatchLoop.LiveAcceptanceCensus(
            ["goal:abcd1234"], new IOException("probe unavailable"));
        var decision = ConductorBatchLoop.DecideLiveAcceptanceAdmission(census, width: 1);
        Assert.False(decision.IsAdmitted);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            ConductorBatchLoop.AdmissionDeniedHeld(
                goal, ConductorAutonomyPolicy.Conservative, decision).Outcome);

        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Contains("live acceptance census unavailable", held.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AcceptanceAdmission_WidthAvailable_HasNoOwner()
    {
        var decision = ConductorBatchLoop.DecideLiveAcceptanceAdmission(
            new ConductorBatchLoop.LiveAcceptanceCensus([]), width: 1);

        Assert.True(decision.IsAdmitted);
        Assert.Equal(ConductorHoldOwner.None, decision.Owner);
    }
}
