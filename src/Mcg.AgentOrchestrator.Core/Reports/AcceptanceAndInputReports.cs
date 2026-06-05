namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalAcceptanceSummary(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    bool IsAccepted,
    int TotalTasks,
    int PassedTasks,
    int OpenVerificationCount,
    int PendingHumanInputCount,
    IReadOnlyList<GoalAcceptanceBlocker> Blockers);

public sealed record GoalAcceptanceBlocker(
    GoalAcceptanceBlockerKind Kind,
    TaskId? TaskId,
    HumanInputRequestId? HumanInputRequestId,
    string Message,
    string SuggestedAction);

public sealed record GoalHumanInputWorklist(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    int OpenCount,
    IReadOnlyList<HumanInputWorkItem> Items);

public sealed record HumanInputWorkItem(
    HumanInputRequestId RequestId,
    TaskId? TaskId,
    AgentRole? Role,
    string? Description,
    WorkTaskStatus? TaskStatus,
    string Question,
    DateTimeOffset RequestedAt,
    string SuggestedAction);
