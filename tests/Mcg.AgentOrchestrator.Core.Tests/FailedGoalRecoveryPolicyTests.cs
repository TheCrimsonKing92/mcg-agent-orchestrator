using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryPolicyTests
{
    [Fact]
    public void EqualFactsReturnEqualDecisionWithoutMutation()
    {
        var task = Task(outcome: DispatchOutcomeKind.PreflightFailure);
        var firstFacts = Facts([task]);
        var secondFacts = Facts([task with { }]);

        var first = FailedGoalRecoveryPolicy.Evaluate(firstFacts);
        var second = FailedGoalRecoveryPolicy.Evaluate(secondFacts);

        Assert.Equal(firstFacts, secondFacts);
        Assert.Equal(first, second);
        Assert.Equal(0, firstFacts.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, firstFacts.Tasks.Single().EmptyOutputRetryCount);
    }

    [Fact]
    public void LiveSiblingPrecedesExitedResultAndRemovingItSelectsReconciliation()
    {
        var task = Task(live: true, exited: true);

        AssertDecision(Facts([task]), FailedGoalRecoveryAction.Hold, 1);
        AssertDecision(Facts([task with { HasLiveProcess = false }]), FailedGoalRecoveryAction.ReconcileExitedDispatch, 2);
    }

    [Fact]
    public void ExitedResultPrecedesTesterInconclusiveAndRemovingItSelectsTesterRetry()
    {
        var task = Task(role: AgentRole.Tester, exited: true, outcome: DispatchOutcomeKind.VerificationInconclusive);

        AssertDecision(Facts([task]), FailedGoalRecoveryAction.ReconcileExitedDispatch, 2);
        var retry = AssertDecision(Facts([task with { IsExitedWithoutAppliedCompletion = false }]), FailedGoalRecoveryAction.RetryTransient, 3);
        Assert.Equal(RetryCause.EnvironmentApparatusFailure, retry.RetryCause);
    }

    [Fact]
    public void TesterInconclusivePrecedesStaleRecoveryAndRoleCounterfactualSelectsStale()
    {
        var task = Task(
            role: AgentRole.Tester,
            outcome: DispatchOutcomeKind.VerificationInconclusive,
            stale: FailedGoalStaleRecoveryDisposition.Retry);

        AssertDecision(Facts([task]), FailedGoalRecoveryAction.RetryTransient, 3);
        AssertDecision(Facts([task with { RequiredRole = AgentRole.Developer }]), FailedGoalRecoveryAction.RetryStale, 4);
    }

    [Fact]
    public void NonRetryableStaleRecoveryEscalatesInsteadOfFallingThroughToPreflight()
    {
        var task = Task(
            outcome: DispatchOutcomeKind.PreflightFailure,
            stale: FailedGoalStaleRecoveryDisposition.Escalate);

        var decision = AssertDecision(Facts([task]), FailedGoalRecoveryAction.Escalate, 4);
        Assert.Contains("stale diagnostic", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightPrecedesRealFailureAndRemovingItSelectsCriterionRetry()
    {
        var task = Task(
            outcome: DispatchOutcomeKind.PreflightFailure,
            recommendation: RecoveryRecommendation.AutoRetry,
            outcomeClass: TaskOutcomeClass.RealFailure,
            cause: RetryCause.NewSourceFinding);

        AssertDecision(Facts([task]), FailedGoalRecoveryAction.RetryTransient, 5);
        AssertDecision(Facts([task with { OutcomeKind = DispatchOutcomeKind.UnknownFailure }]), FailedGoalRecoveryAction.CriterionRetry, 6);
    }

    [Fact]
    public void RealFailurePrecedesFlakeAndMissingTypedCauseEscalatesLoudly()
    {
        var task = Task(
            outcome: DispatchOutcomeKind.LaunchFailure,
            recommendation: RecoveryRecommendation.AutoRetry,
            outcomeClass: TaskOutcomeClass.RealFailure,
            cause: RetryCause.NewTestFinding,
            retries: 1);

        AssertDecision(Facts([task]), FailedGoalRecoveryAction.CriterionRetry, 6);
        var missingCause = AssertDecision(Facts([task with { AutomaticRetryCause = null }]), FailedGoalRecoveryAction.Escalate, 6);
        Assert.Contains("without a typed retry cause", missingCause.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTransientBudgetRetriesAtBoundaryAndEscalatesAboveIt()
    {
        var atBoundary = Task(outcome: DispatchOutcomeKind.EmptyOutputFlake, retries: 2);
        var facts = Facts([atBoundary], maxTransient: 2);

        AssertDecision(facts, FailedGoalRecoveryAction.RetryTransient, 7);
        AssertDecision(Facts([atBoundary with { EmptyOutputRetryCount = 3 }], maxTransient: 2), FailedGoalRecoveryAction.Escalate, 7);
    }

    [Fact]
    public void MissingEvidenceIsHeldAfterFindingObservationInsteadOfGuessed()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(
            Facts([Task(outcome: null)])
                .WithReviewContractObservation(null)
                .WithVerifyingFindingObservation(null));

        Assert.Equal(FailedGoalRecoveryAction.Hold, decision.Action);
        Assert.Equal("missing-current-failure-evidence", decision.DiscriminatingEvidence);
    }

    [Fact]
    public void ProviderInterruptionPreservesExistingTerminalEscalation()
    {
        var task = Task(
            outcome: DispatchOutcomeKind.ProviderConnectivity,
            recommendation: RecoveryRecommendation.AutoRetry,
            outcomeClass: TaskOutcomeClass.Environmental,
            cause: RetryCause.ProviderInterruption);

        var first = FailedGoalRecoveryPolicy.Evaluate(Facts([task]));
        Assert.Equal(FailedGoalRecoveryAction.ObserveReviewContract, first.Action);
        var second = FailedGoalRecoveryPolicy.Evaluate(Facts([task]).WithReviewContractObservation(null));
        Assert.Equal(FailedGoalRecoveryAction.ObserveVerifyingFinding, second.Action);
        AssertDecision(
            Facts([task])
                .WithReviewContractObservation(null)
                .WithVerifyingFindingObservation(null),
            FailedGoalRecoveryAction.Escalate,
            9);
    }

    [Fact]
    public void OperatorDispositionAndFindingAttributionRemainTyped()
    {
        var observation = new FailedGoalFindingObservation(
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
            new TaskId("reviewer-task"),
            "verification:42:none",
            "operator disposition required",
            Attribution: "operator-owned-evidence");

        var decision = FailedGoalRecoveryPolicy.Evaluate(
            Facts([Task()])
                .WithReviewContractObservation(null)
                .WithVerifyingFindingObservation(observation));

        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal(8, decision.DiscriminatingRung);
        Assert.Equal("operator-owned-evidence", decision.DiscriminatingEvidence);
        Assert.Equal(new TaskId("reviewer-task"), decision.Identity.TaskId);
    }

    [Fact]
    public void ReviewContractObservationPrecedesVerifyingFindingAndCounterfactualSelectsVerifyingRoute()
    {
        var contract = FailedGoalFindingObservation.Observed(
            FailedGoalFindingObservationKind.ReviewRetryCapReached,
            "contract repair requires operator evidence");
        var verifying = FailedGoalFindingObservation.Routed(
            FailedGoalFindingObservationKind.FindingRouteObserved,
            new TaskId("developer-task"),
            "verification:84:none",
            "retry routed blocking finding",
            warningMessage: null,
            observedCause: RetryCause.NewSourceFinding);
        var facts = Facts([Task()])
            .WithReviewContractObservation(contract)
            .WithVerifyingFindingObservation(verifying);

        var contractDecision = AssertDecision(facts, FailedGoalRecoveryAction.Escalate, 8);
        Assert.Contains("contract repair", contractDecision.Reason, StringComparison.Ordinal);

        var verifyingDecision = AssertDecision(
            Facts([Task()])
                .WithReviewContractObservation(null)
                .WithVerifyingFindingObservation(verifying),
            FailedGoalRecoveryAction.FindingRetry,
            8);
        Assert.Equal(new TaskId("developer-task"), verifyingDecision.Identity.TaskId);
        Assert.Equal(RetryCause.NewSourceFinding, verifyingDecision.RetryCause);
    }

    [Fact]
    public void PolicyDoesNotInventUnchangedContextRetry()
    {
        var actionNames = Enum.GetNames<FailedGoalRecoveryAction>();

        Assert.DoesNotContain(nameof(RetryCause.UnchangedContextRepeat), actionNames);
    }

    private static FailedGoalRecoveryDecision AssertDecision(
        FailedGoalRecoveryFacts facts,
        FailedGoalRecoveryAction action,
        int rung)
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        return decision;
    }

    private static FailedGoalRecoveryFacts Facts(
        IEnumerable<FailedGoalRecoveryTaskFacts> tasks,
        int maxTransient = 2) =>
        new(
            new GoalId("goal-policy"),
            GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: 0,
            maxCriterionRetries: 2,
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
