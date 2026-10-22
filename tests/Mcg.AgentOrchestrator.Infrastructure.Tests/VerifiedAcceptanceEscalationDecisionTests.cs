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

    [Xunit.Fact]
    public void Acknowledgement_SameTimestampAndHydration_MatchesOrderedOccurrence()
    {
        var (kernel, goal) = CreateGoal();
        Escalate(kernel, goal);
        kernel.ParkGoal(goal.Id, "acknowledge interrupted acceptance");
        kernel.UnparkGoal(goal.Id, "reconsider normally");
        var snapshot = kernel.ExportSnapshot();
        var sameTime = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        snapshot = snapshot with { Goals = snapshot.Goals.Select(g => g with
        {
            Timeline = g.Timeline.Select(e => e with { OccurredAt = sameTime }).ToArray()
        }).ToArray() };
        var reloaded = AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(goal.Id);
        // Capture before acknowledgement, then use a separately hydrated final history.
        var before = snapshot with { Goals = snapshot.Goals.Select(g => g with
        {
            Timeline = g.Timeline.Take(g.Timeline.Count - 2).ToArray()
        }).ToArray() };
        var identity = Assert.IsType<VerifiedAcceptanceEscalationDecision.EventIdentity>(
            VerifiedAcceptanceEscalationDecision.FindUnresolvedEscalation(
                AgentOrchestratorKernel.FromSnapshot(before).GetGoal(goal.Id)));
        var acknowledgement = Assert.IsType<VerifiedAcceptanceEscalationDecision.EventIdentity>(
            VerifiedAcceptanceEscalationDecision.FindAcknowledgement(reloaded, identity));
        Assert.Equal(identity.TimelineIndex + 1, acknowledgement.TimelineIndex);
        Assert.False(VerifiedAcceptanceEscalationDecision.HasPersistedVerifiedAcceptanceEscalation(reloaded));
        Assert.Equal(acknowledgement, VerifiedAcceptanceEscalationDecision.FindAcknowledgement(
            AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(goal.Id), identity));
    }

    [Xunit.Theory]
    [Xunit.InlineData("unresolved")]
    [Xunit.InlineData("unrelated")]
    [Xunit.InlineData("older-ack")]
    [Xunit.InlineData("newer-failure")]
    [Xunit.InlineData("missing-history")]
    [Xunit.InlineData("wrong-occurrence")]
    public void Acknowledgement_WithoutMatchingClearedEpisode_Holds(string scenario)
    {
        var (kernel, goal) = CreateGoal();
        if (scenario == "older-ack")
        {
            kernel.ParkGoal(goal.Id, "old acknowledgement");
            kernel.UnparkGoal(goal.Id, "old resume");
        }
        Escalate(kernel, goal);
        var identity = VerifiedAcceptanceEscalationDecision.FindUnresolvedEscalation(goal)!;
        if (scenario == "unrelated") kernel.RecordGoalPolicyDecision(goal.Id, "ordinary note and tick");
        if (scenario is "newer-failure" or "wrong-occurrence")
        {
            kernel.ParkGoal(goal.Id, "acknowledgement");
            kernel.UnparkGoal(goal.Id, "resume");
            Escalate(kernel, goal);
        }
        if (scenario == "missing-history") goal = CreateGoal().Goal;
        if (scenario == "wrong-occurrence") identity = identity with { TimelineIndex = identity.TimelineIndex + 1 };
        Assert.Null(VerifiedAcceptanceEscalationDecision.FindAcknowledgement(goal, identity));
    }

    private static void Escalate(AgentOrchestratorKernel kernel, Goal goal) =>
        kernel.RecordGoalPolicyDecision(goal.Id, "interrupted acceptance",
            new ConductorTickOutcomePayload("Escalated", "Verified", "BackgroundAcceptanceFailed"));
}
