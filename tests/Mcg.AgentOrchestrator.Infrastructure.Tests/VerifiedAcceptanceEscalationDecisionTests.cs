using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class VerifiedAcceptanceEscalationDecisionTests
{
    [Xunit.Fact]
    public void TypedAcceptanceEscalationSurvivesRewording()
    {
        var (kernel, goal) = CreateGoal();
        kernel.RecordGoalPolicyDecision(goal.Id, "Review is required.",
            new ConductorTickOutcomePayload("Escalated", "Verified", nameof(ConductorEscalationKind.AcceptanceVerificationFailed)));

        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Held", "Verified", "AcceptanceVerificationFailed")]
    [Xunit.InlineData("Escalated", "CleanedUp", "AcceptanceVerificationFailed")]
    [Xunit.InlineData("Escalated", "Verified", "Unspecified")]
    [Xunit.InlineData("Escalated", "Verified", "FutureKind")]
    public void TypedPayloadOverridesLegacyMessage(string outcome, string state, string kind)
    {
        var (kernel, goal) = CreateGoal();
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed",
            new ConductorTickOutcomePayload(outcome, state, kind));

        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
    }

    [Xunit.Fact]
    public void LegacyEventAndClearingEventsKeepTheirOriginalOrder()
    {
        var (kernel, goal) = CreateGoal();
        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 1: escalated at Verified — Acceptance verification failed");
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));

        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 2: held at Verified — waiting",
            new ConductorTickOutcomePayload("Held", "Verified", null));
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));

        kernel.RecordGoalPolicyDecision(goal.Id, "Goal parked: operator action");
        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));

        kernel.RecordGoalPolicyDecision(goal.Id, "Batch loop tick 3: escalated at Verified — background acceptance failed");
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
        kernel.RecordGoalPolicyDecision(goal.Id, "Set-aside self-cleared: condition=lifecycle_escalation");
        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
    }

    [Xunit.Fact]
    public void LiveOutcomeUsesKindBeforeReasonText()
    {
        var typed = Result(new ConductorAdvanceOutcome.Escalated(
            GoalLifecycleState.Verified, "Review is required.", ConductorEscalationKind.AcceptanceVerificationFailed));
        var other = Result(new ConductorAdvanceOutcome.Escalated(
            GoalLifecycleState.Verified, "Acceptance verification failed", ConductorEscalationKind.BackgroundAcceptanceFailed));
        var legacy = Result(new ConductorAdvanceOutcome.Escalated(
            GoalLifecycleState.Verified, "Acceptance verification failed"));

        Assert.True(VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(typed));
        Assert.False(VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(other));
        Assert.True(VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(legacy));
    }

    [Xunit.Fact]
    public void BackgroundKindStandsWithoutTriggeringSerialRetry()
    {
        var outcome = new ConductorAdvanceOutcome.Escalated(
            GoalLifecycleState.Verified, "A background gate stopped.", ConductorEscalationKind.BackgroundAcceptanceFailed);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        var (kernel, goal) = CreateGoal();
        kernel.RecordGoalPolicyDecision(goal.Id, "A background gate stopped.", payload);

        Assert.Equal(nameof(ConductorEscalationKind.BackgroundAcceptanceFailed), payload.EscalationKind);
        Assert.True(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(goal));
        Assert.False(VerifiedAcceptanceEscalationDecision.IsTransientVerificationFailure(Result(outcome)));
    }

    [Xunit.Theory]
    [Xunit.InlineData(ProgressKind.TaskRetried, "retry")]
    [Xunit.InlineData(ProgressKind.GoalCancelled, "cancel")]
    [Xunit.InlineData(ProgressKind.GoalSuperseded, "supersede")]
    [Xunit.InlineData(ProgressKind.HumanInputRequested, "Goal parked: waiting")]
    [Xunit.InlineData(ProgressKind.GoalPolicyDecision, "Goal parked: waiting")]
    [Xunit.InlineData(ProgressKind.GoalPolicyDecision, "Set-aside self-cleared: blocker resolved")]
    [Xunit.InlineData(ProgressKind.HumanInputReceived, "answer")]
    public void ExistingClearingEventsRemainRecognized(ProgressKind kind, string message)
    {
        var evt = new ProgressEvent(GoalId.New(), null, kind, message, DateTimeOffset.UtcNow);

        Assert.True(VerifiedAcceptanceEscalationDecision.ClearsPersistedVerifiedAcceptanceEscalation(evt));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        return (kernel, kernel.CreateGoal("Check standing acceptance escalation."));
    }

    private static ConductorAdvanceResult Result(ConductorAdvanceOutcome outcome) =>
        new("goal", "goal", "conservative", outcome);
}
