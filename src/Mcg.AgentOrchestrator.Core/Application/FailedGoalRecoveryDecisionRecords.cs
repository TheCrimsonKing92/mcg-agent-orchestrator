namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Records failed-goal recovery decisions without owning effects. Policy rungs currently use 1–9;
/// driver rungs start at 101, leaving room for policy growth without collisions. New driver rungs
/// extend this block upward; policy rungs must remain below the driver block.
/// </summary>
public static class FailedGoalRecoveryDecisionRecords
{
    public const string StageName = "failed-goal-recovery";
    public const int DriverRungBlockStart = 101;

    public static PolicyDecisionRecord FromPolicy(FailedGoalRecoveryDecision decision) => new(
        StageName, decision.Action.ToString(), decision.DiscriminatingRung,
        decision.DiscriminatingEvidence, decision.Reason, Facts(decision.Identity));

    public static PolicyDecisionRecord StaleRecoveryFacts(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart, "stale-recovery-facts", reason);

    public static PolicyDecisionRecord CandidateRedLifecycleConflict(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 1, "candidate-red-lifecycle-conflict", reason);

    public static PolicyDecisionRecord ExitedUnappliedProcessRecord(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart + 2, "exited-unapplied-process-record", reason);

    public static PolicyDecisionRecord ReconcileBeforeFailureHandling(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart + 3, "reconcile-before-failure-handling", reason);

    public static PolicyDecisionRecord BuildLogUnavailable(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 4, "build-log-unavailable", reason);

    public static PolicyDecisionRecord WorktreeUnavailable(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 5, "worktree-unavailable", reason);

    public static PolicyDecisionRecord WorktreeInspectionUnavailable(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 6, "worktree-inspection-unavailable", reason);

    public static PolicyDecisionRecord CheckpointCommitFailed(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 7, "checkpoint-commit-failed", reason);

    public static PolicyDecisionRecord CheckpointDirtyAfterCommit(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 8, "checkpoint-dirty-after-commit", reason);

    public static PolicyDecisionRecord BuildCheckRecoveryExhausted(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 9, "build-check-recovery-exhausted", reason);

    public static PolicyDecisionRecord BuildCheckRetryFailed(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 10, "build-check-retry-failed", reason);

    public static PolicyDecisionRecord BuildCheckRetryApplied(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart + 11, "build-check-retry-applied", reason);

    public static PolicyDecisionRecord TimedOutRerunObservationEscalated(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 12, "timed-out-rerun-observation-escalated", reason);

    public static PolicyDecisionRecord TimedOutRerunRetryAuthorityChanged(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart + 13, "timed-out-rerun-retry-authority-changed", reason);

    public static PolicyDecisionRecord TimedOutRoundUnchanged(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Escalate, DriverRungBlockStart + 14, "timed-out-round-unchanged", reason);

    public static PolicyDecisionRecord TimedOutRerunEvidencePending(FailedGoalRecoveryIdentity identity, string reason) =>
        FromDriver(identity, FailedGoalRecoveryAction.Hold, DriverRungBlockStart + 15, "timed-out-rerun-evidence-pending", reason);

    private static PolicyDecisionRecord FromDriver(
        FailedGoalRecoveryIdentity identity, FailedGoalRecoveryAction action, int rung, string evidence, string reason) =>
        new(StageName, action.ToString(), rung, evidence, reason, Facts(identity));

    private static IReadOnlyList<PolicyDecisionFact> Facts(FailedGoalRecoveryIdentity identity) =>
        identity.TaskId is { } taskId
            ? Array.AsReadOnly<PolicyDecisionFact>(
            [
                new("goalId", identity.GoalId.Value),
                new("targetTaskId", taskId.Value),
                new("contextVersion", identity.ContextVersion)
            ])
            : Array.AsReadOnly<PolicyDecisionFact>(
            [
                new("goalId", identity.GoalId.Value),
                new("contextVersion", identity.ContextVersion)
            ]);
}
