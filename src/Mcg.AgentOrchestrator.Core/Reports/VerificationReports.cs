namespace Mcg.AgentOrchestrator.Core;

public enum VerificationGateReason
{
    Passed,
    NotReady,
    MissingVerification,
    VerificationFailed,
    OutputTokenLimit,
    ReviewerWorkerResultBlocker,
    DirtyUsefulRecovery,
    DirtyUnverifiedRecovery,
    MissingExecutedTestReceipt
}

public sealed record GoalVerificationGate(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    bool IsSatisfied,
    IReadOnlyList<TaskVerificationGate> Tasks,
    VerificationGateReason Reason = VerificationGateReason.Passed,
    string Message = "Goal verification gate passed.");

public sealed record TaskVerificationGate(
    TaskId TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    VerificationGateStatus GateStatus,
    string Message,
    VerificationGateReason Reason);

public sealed record GoalVerificationWorklist(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    bool IsSatisfied,
    int OpenCount,
    IReadOnlyList<TaskVerificationWorkItem> Items);

public sealed record TaskVerificationWorkItem(
    TaskId TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    VerificationGateStatus GateStatus,
    string Message,
    string SuggestedAction);
