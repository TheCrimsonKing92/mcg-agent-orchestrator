namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalStageReadinessReport(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    int TotalStages,
    int VerifiedStages,
    int OpenStages,
    int BlockedStages,
    bool IsReadyForAcceptance,
    IReadOnlyList<TaskStageReadiness> Stages);

public sealed record TaskStageReadiness(
    TaskId TaskId,
    AgentRole Stage,
    string Description,
    WorkTaskStatus TaskStatus,
    bool IsAssigned,
    StageReadinessStatus StageStatus,
    TaskEvidenceKind LatestEvidence,
    VerificationGateStatus VerificationStatus,
    int PendingHumanInputCount,
    string Message,
    string SuggestedAction);

public sealed record GoalNextActions(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<NextActionItem> Items);

public sealed record NextActionItem(
    NextActionKind Kind,
    TaskId? TaskId,
    HumanInputRequestId? HumanInputRequestId,
    string Message,
    string? ResumeCommand = null);

public sealed record NextActionAutomationPlan(
    bool CanExecute,
    NextActionAutomationKind Kind,
    TaskId? TaskId,
    string Message);
