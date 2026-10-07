using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryDecisionRecordsTests
{
    [Theory]
    [InlineData(FailedGoalRecoveryAction.Hold, 1, "live-sibling-or-owned-attempt")]
    [InlineData(FailedGoalRecoveryAction.Escalate, 6, "real-failure-missing-typed-cause-no-budget-spend")]
    public void PolicyOutcomePreservesItsOwnDecisionAndIdentity(
        FailedGoalRecoveryAction action, int rung, string evidence)
    {
        var task = new FailedGoalRecoveryTaskFacts(
            new TaskId("target-task"), AgentRole.Developer, WorkTaskStatus.Failed,
            HasLiveProcess: action == FailedGoalRecoveryAction.Hold,
            IsExitedWithoutAppliedCompletion: false, AttemptIdentity: "process:7:42",
            OutcomeKind: DispatchOutcomeKind.UnknownFailure, RecoveryRecommendation: RecoveryRecommendation.AutoRetry,
            OutcomeClass: TaskOutcomeClass.RealFailure, EvidenceSummary: "command failed",
            StaleRecoveryDisposition: FailedGoalStaleRecoveryDisposition.None, StaleRecoveryDiagnostic: null,
            EmptyOutputRetryCount: 0, CriterionRetryCount: 0, AutomaticRetryCause: null,
            ProviderFailureKind: null, ExitCode: 1, Command: "verify", RetryBackoff: TimeSpan.Zero);
        var facts = new FailedGoalRecoveryFacts(
            new GoalId("goal-record"), GoalLifecycleState.Failed, 0, 2, 2,
            "context-v1", [task], "terminal failure");
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        Assert.Equal(action, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);

        var record = FailedGoalRecoveryDecisionRecords.FromPolicy(decision);

        Assert.Equal("failed-goal-recovery", record.Stage);
        Assert.Equal(action.ToString(), record.Action);
        Assert.Equal(decision.DiscriminatingRung, record.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, record.DiscriminatingEvidence);
        Assert.Equal(decision.Reason, record.Reason);
        AssertFact(record, "goalId", "goal-record");
        AssertFact(record, "contextVersion", "context-v1");
        AssertFact(record, "targetTaskId", "target-task");
    }

    [Fact]
    public void DriverRungsHaveDistinctEvidenceOutsideThePolicyBlock()
    {
        var identity = new FailedGoalRecoveryIdentity(new GoalId("goal-record"), new TaskId("target-task"), null, "context-v2");
        const string reason = "Existing outcome reason.";
        var records = new[]
        {
            FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(identity, reason),
            FailedGoalRecoveryDecisionRecords.CandidateRedLifecycleConflict(identity, reason),
            FailedGoalRecoveryDecisionRecords.ExitedUnappliedProcessRecord(identity, reason),
            FailedGoalRecoveryDecisionRecords.ReconcileBeforeFailureHandling(identity, reason)
        };

        Assert.Equal(new[] { 101, 102, 103, 104 }, records.Select(record => record.Rung));
        Assert.Equal(new[]
        {
            "stale-recovery-facts", "candidate-red-lifecycle-conflict",
            "exited-unapplied-process-record", "reconcile-before-failure-handling"
        }, records.Select(record => record.DiscriminatingEvidence));
        Assert.Equal(new[] { "Hold", "Escalate", "Hold", "Hold" }, records.Select(record => record.Action));
        // FailedGoalRecoveryPolicy.Evaluate and its timed-out rerun rule use policy rungs 1–9.
        var policyRungs = Enumerable.Range(1, 9).ToArray();
        Assert.All(records, record =>
        {
            Assert.DoesNotContain(record.Rung, policyRungs);
            Assert.True(record.Rung >= FailedGoalRecoveryDecisionRecords.DriverRungBlockStart);
            Assert.Equal("failed-goal-recovery", record.Stage);
            Assert.Equal(reason, record.Reason);
            AssertFact(record, "goalId", "goal-record");
            AssertFact(record, "contextVersion", "context-v2");
            AssertFact(record, "targetTaskId", "target-task");
        });
    }

    [Fact]
    public void MissingTargetIsOmittedForPolicyAndDriverRecords()
    {
        var identity = new FailedGoalRecoveryIdentity(new GoalId("goal-record"), null, null, "context-v3");
        var decision = new FailedGoalRecoveryDecision(
            FailedGoalRecoveryAction.Escalate, identity, 9, "failed-terminal-fallthrough", "terminal failure");
        var records = new[]
        {
            FailedGoalRecoveryDecisionRecords.FromPolicy(decision),
            FailedGoalRecoveryDecisionRecords.StaleRecoveryFacts(identity, "stale"),
            FailedGoalRecoveryDecisionRecords.CandidateRedLifecycleConflict(identity, "conflict"),
            FailedGoalRecoveryDecisionRecords.ExitedUnappliedProcessRecord(identity, "unapplied"),
            FailedGoalRecoveryDecisionRecords.ReconcileBeforeFailureHandling(identity, "reconciled")
        };

        Assert.All(records, record =>
        {
            Assert.Equal(2, record.Facts.Count);
            AssertFact(record, "goalId", "goal-record");
            AssertFact(record, "contextVersion", "context-v3");
            Assert.DoesNotContain(record.Facts, fact => fact.Name == "targetTaskId");
        });
    }

    private static void AssertFact(PolicyDecisionRecord record, string name, string value) =>
        Assert.Equal(value, Assert.Single(record.Facts, fact => fact.Name == name).Value);
}
