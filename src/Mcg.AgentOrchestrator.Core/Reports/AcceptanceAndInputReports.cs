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
    IReadOnlyList<GoalAcceptanceBlocker> Blockers,
    IReadOnlyList<GoalAcceptanceOutcome> Outcomes);

public sealed record GoalAcceptanceOutcome(
    string Outcome,
    bool IsCurrentCandidate,
    DateTimeOffset OccurredAt,
    string Message);

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
    HumanWaitKind Kind,
    bool IsAutoDefaultable,
    bool IsDismissible,
    bool IsAnswerRequired,
    bool IsExternallyBlocked,
    long AgeSeconds,
    string ResumeCommand,
    string SuggestedAction,
    int TotalRequestCount = 0,
    int OpenRequestCount = 0,
    int OccurrenceCount = 1);
