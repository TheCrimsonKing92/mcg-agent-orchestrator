using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Core;

public enum FailedGoalVerifyingFindingRouteKind
{
    OperatorEvidenceRequired,
    TargetUnavailable,
    RetryCapReached,
    MissingFindingResult,
    RepeatedFailingTestSet,
    Routed,
    LifetimeBackstopReached
}

public sealed record FailedGoalVerifyingFindingRouteFacts(
    TaskId TriggeringTaskId,
    AgentRole TriggeringRole,
    string TriggerAttemptIdentity,
    TaskId? ExplicitTargetTaskId,
    bool RequiresCommittedTarget,
    AgentRole? ReviewerTargetRole,
    bool ReviewerEscalatesToOperator,
    int Round,
    int StopRound,
    int WarningRound,
    bool MissingFindingResult,
    RetryCause? ObservedCause,
    ImmutableArray<FailedGoalFindingRouteTask> PriorTasks,
    PreReviewRepeatedFailureSummary? RepeatedFailure = null,
    int LifetimeRound = 0,
    int LifetimeBackstop = 0);

public sealed record FailedGoalVerifyingFindingRouteSelection(
    FailedGoalVerifyingFindingRouteKind Kind,
    TaskId? TargetTaskId,
    AgentRole TargetRole,
    string AttemptIdentity,
    int Round,
    RetryCause? RetryCause,
    RetryRoundKind? RoundKind,
    bool EmitWarning);
