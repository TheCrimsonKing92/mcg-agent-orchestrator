namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalMonitor(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    IReadOnlyList<TaskStatusCount> TaskStatusCounts,
    int PendingHumanInputCount,
    IReadOnlyList<TaskAttentionItem> AttentionItems,
    DateTimeOffset? LastTimelineEventAt);

public sealed record TaskStatusCount(WorkTaskStatus Status, int Count);

public sealed record TaskAttentionItem(TaskAttentionKind Kind, TaskId? TaskId, string Message);

public sealed record TaskQuery(
    WorkTaskStatus? Status = null,
    AgentRole? Role = null,
    string? IdPrefix = null,
    TaskEvidenceKind? Evidence = null,
    ProgressKind? EventKind = null);

public sealed record TaskQueryResult(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    TaskQuery Query,
    IReadOnlyList<TaskSpec> Tasks);

public sealed record GoalEvidenceSummary(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    int TasksWithExecution,
    int TasksWithDispatch,
    int TasksWithProcess,
    int RunningProcesses,
    int TasksWithVerification,
    int PassedVerifications,
    int FailedVerifications,
    int PendingHumanInputCount,
    int? InputTokens,
    int? OutputTokens,
    int? PotentiallyPaidInputTokens,
    int? PotentiallyPaidOutputTokens,
    IReadOnlyList<ModelUsageSummary> ModelUsage,
    IReadOnlyList<DispatchModelSummary> DispatchModelUsage,
    IReadOnlyList<TaskEvidenceSummary> Tasks);

public sealed record ModelUsageSummary(
    string ProviderName,
    string ModelName,
    int ExecutionCount,
    int? InputTokens,
    int? OutputTokens,
    int OutputTokenLimitHitCount = 0,
    int? MaxOutputTokens = null,
    TaskComplexity? TaskComplexity = null,
    bool IsPotentiallyPaidProvider = false);

public sealed record DispatchModelSummary(
    string ProviderName,
    string ModelName,
    int DispatchCount,
    TaskComplexity? TaskComplexity = null,
    string? ReasoningEffort = null,
    bool IsPotentiallyPaidProvider = false);

public sealed record TaskEvidenceSummary(
    TaskId TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    TaskEvidenceKind LatestEvidence,
    bool HasExecution,
    bool HasDispatch,
    bool HasProcess,
    bool IsProcessRunning,
    bool HasVerification,
    bool? LatestVerificationSucceeded,
    int VerificationHistoryCount,
    int PendingHumanInputCount,
    string Message);
