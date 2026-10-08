using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryPolicyPassingEvidenceTests
{
    [Fact]
    public void PassingEvidenceOpenFindingUsesExistingDeveloperRetryWithNewSourceFindingCause()
    {
        var observation = FailedGoalFindingObservation.Routed(
            FailedGoalFindingObservationKind.FindingActionableRed,
            new TaskId("developer-task"), "verification:42:none",
            "PASSING_EVIDENCE_OPEN_FINDING candidate_sha=abc1234; receipt_ids=green-receipt; finding_ids=open-finding",
            warningMessage: null);

        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Task()])
            .WithReviewContractObservation(null).WithVerifyingFindingObservation(observation));

        Assert.Equal(FailedGoalRecoveryAction.FindingRetry, decision.Action);
        Assert.Equal(new TaskId("developer-task"), decision.Identity.TaskId);
        Assert.Equal(RetryCause.NewSourceFinding, decision.RetryCause);
        Assert.Equal(8, decision.DiscriminatingRung);
    }

    [Fact]
    public void PassingEvidenceWithoutDeveloperUsesExistingNonCapEscalation()
    {
        const string evidence = "PASSING_EVIDENCE_OPEN_FINDING candidate_sha=abc1234; receipt_ids=green-receipt; finding_ids=open-finding";
        var observation = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable, evidence);

        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Task()])
            .WithReviewContractObservation(null).WithVerifyingFindingObservation(observation));

        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal(8, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.Reason);
        Assert.Null(decision.RetryCause);
    }

    private static FailedGoalRecoveryFacts Facts(
        IEnumerable<FailedGoalRecoveryTaskFacts> tasks,
        int maxTransient = 2,
        int automaticRetryCount = 0,
        int maxCriterionRetries = 2) =>
        new(
            new GoalId("goal-policy"),
            GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: automaticRetryCount,
            maxCriterionRetries,
            maxTransient,
            contextVersion: "context-v1",
            tasks,
            terminalEscalationReason: "terminal failure");

    private static FailedGoalRecoveryTaskFacts Task(
        AgentRole role = AgentRole.Developer,
        bool live = false,
        bool exited = false,
        DispatchOutcomeKind? outcome = DispatchOutcomeKind.UnknownFailure,
        RecoveryRecommendation recommendation = RecoveryRecommendation.None,
        TaskOutcomeClass outcomeClass = TaskOutcomeClass.UnknownEra,
        FailedGoalStaleRecoveryDisposition stale = FailedGoalStaleRecoveryDisposition.None,
        RetryCause? cause = null,
        int retries = 0) =>
        new(
            new TaskId("task-policy"),
            role,
            WorkTaskStatus.Failed,
            live,
            exited,
            "process:7:42",
            outcome,
            recommendation,
            outcomeClass,
            "evidence",
            stale,
            "stale diagnostic",
            retries,
            CriterionRetryCount: 0,
            cause,
            ProviderFailureKind: null,
            ExitCode: 1,
            Command: "verify",
            RetryBackoff: TimeSpan.Zero);
}
