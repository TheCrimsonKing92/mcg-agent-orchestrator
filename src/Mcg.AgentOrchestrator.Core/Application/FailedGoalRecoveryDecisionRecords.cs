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
