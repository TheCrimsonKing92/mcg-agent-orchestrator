using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record StatusCountDto(WorkTaskStatus Status, int Count);

internal sealed record AttentionDto(TaskAttentionKind Kind, string? TaskId, string Message);

internal sealed record HumanInputDto(
    string Id,
    string GoalId,
    string? TaskId,
    int? TaskNumber,
    string Question,
    DateTimeOffset RequestedAt,
    bool IsCompleted,
    string? Answer,
    DateTimeOffset? AnsweredAt);

internal sealed record HumanInputWorklistDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int OpenCount,
    IReadOnlyList<HumanInputWorkItemDto> Items);

internal sealed record HumanInputWorkItemDto(
    string RequestId,
    string? TaskId,
    int? TaskNumber,
    AgentRole? Role,
    string? Description,
    WorkTaskStatus? TaskStatus,
    string Question,
    DateTimeOffset RequestedAt,
    string SuggestedAction,
    string SuggestedCommand);

internal sealed record MonitorDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    IReadOnlyList<StatusCountDto> StatusCounts,
    int PendingHumanInputCount,
    IReadOnlyList<AttentionDto> Attention,
    DateTimeOffset? LastTimelineEventAt);

internal sealed record GoalAcceptanceSummaryDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    bool IsAccepted,
    int TotalTasks,
    int PassedTasks,
    int OpenVerificationCount,
    int PendingHumanInputCount,
    IReadOnlyList<GoalAcceptanceBlockerDto> Blockers);

internal sealed record GoalAcceptanceBlockerDto(
    GoalAcceptanceBlockerKind Kind,
    string? TaskId,
    int? TaskNumber,
    string? HumanInputRequestId,
    string Message,
    string SuggestedAction,
    string SuggestedCommand);

internal sealed record GoalEvidenceSummaryDto(
    string GoalId,
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
    IReadOnlyList<ModelUsageSummaryDto> ModelUsage,
    IReadOnlyList<DispatchModelSummaryDto> DispatchModelUsage,
    IReadOnlyList<TaskEvidenceSummaryDto> Tasks);

internal sealed record ModelUsageSummaryDto(
    string ProviderName,
    string ModelName,
    int ExecutionCount,
    int? InputTokens,
    int? OutputTokens,
    int OutputTokenLimitHitCount = 0,
    int? MaxOutputTokens = null,
    TaskComplexity? TaskComplexity = null,
    bool IsPotentiallyPaidProvider = false,
    int? PromptCharacterCount = null);

internal sealed record DispatchModelSummaryDto(
    string ProviderName,
    string ModelName,
    int DispatchCount,
    TaskComplexity? TaskComplexity = null,
    string? ReasoningEffort = null,
    bool IsPotentiallyPaidProvider = false,
    int? PromptCharacterCount = null);

internal sealed record TaskEvidenceSummaryDto(
    int TaskNumber,
    string TaskId,
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

internal sealed record GoalWorkSummaryDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    int PendingHumanInputCount,
    bool VerificationSatisfied,
    NextActionDto? NextAction,
    IReadOnlyList<TaskWorkSummaryDto> Tasks);

internal sealed record TaskWorkSummaryDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    string Evidence,
    string? AssignedAgentId,
    DispatchSummaryDto? LastDispatch,
    ProcessSummaryDto? LastProcess,
    VerificationSummaryDto? LastVerification,
    DateTimeOffset? SubscriptionRetryAfter);

internal sealed record DispatchSummaryDto(
    string WorkerName,
    DateTimeOffset DispatchedAt,
    string? ProviderName = null,
    string? ModelName = null,
    string? ReasoningEffort = null,
    TaskComplexity? TaskComplexity = null,
    int? PromptCharacterCount = null);

internal sealed record ProcessSummaryDto(
    int ProcessId,
    bool IsRunning,
    int? ExitCode,
    bool WasCancelled,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath);

internal sealed record VerificationSummaryDto(
    int ExitCode,
    bool Succeeded,
    DateTimeOffset CompletedAt,
    int HistoryCount);

internal sealed record GoalStageReadinessReportDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int TotalStages,
    int VerifiedStages,
    int OpenStages,
    int BlockedStages,
    bool IsReadyForAcceptance,
    IReadOnlyList<TaskStageReadinessDto> Stages);

internal sealed record TaskStageReadinessDto(
    int TaskNumber,
    string TaskId,
    AgentRole Stage,
    string Description,
    WorkTaskStatus TaskStatus,
    bool IsAssigned,
    StageReadinessStatus StageStatus,
    TaskEvidenceKind LatestEvidence,
    VerificationGateStatus VerificationStatus,
    int PendingHumanInputCount,
    string Message,
    string SuggestedAction,
    string SuggestedCommand);

internal sealed record VerificationGateDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    bool IsSatisfied,
    IReadOnlyList<TaskVerificationGateDto> Tasks);

internal sealed record TaskVerificationGateDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    VerificationGateStatus GateStatus,
    string Message);

internal sealed record VerificationWorklistDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    bool IsSatisfied,
    int OpenCount,
    IReadOnlyList<VerificationWorkItemDto> Items);

internal sealed record VerificationWorkItemDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    VerificationGateStatus GateStatus,
    string Message,
    string SuggestedAction,
    string SuggestedCommand);

internal sealed record NextActionsDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<NextActionDto> Items);

internal sealed record NextActionDto(
    int Priority,
    NextActionKind Kind,
    string? TaskId,
    int? TaskNumber,
    string? HumanInputRequestId,
    string Message,
    string SuggestedCommand,
    NextActionControlDto? Control);

internal sealed record NextActionControlDto(string Label, string Method, string Url);
