using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record TaskQueryDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<TaskSummaryDto> Tasks);

internal sealed record TaskSummaryDto(
    int Number,
    string Id,
    AgentRole Role,
    WorkTaskStatus Status,
    string Description,
    string Evidence,
    string? VerificationPlan,
    string? AssignedAgentId,
    DateTimeOffset? SubscriptionRetryAfter = null);

internal sealed record TaskDetailDto(
    TaskSummaryDto Task,
    ExecutionDto? LastExecution,
    DispatchDto? LastDispatch,
    ProcessDto? LastProcess,
    VerificationDto? LastVerification,
    IReadOnlyList<TimelineDto> Timeline);

internal sealed record ExecutionDto(
    string AgentId,
    string AgentName,
    string ProviderName,
    string ModelName,
    string Output,
    string StopReason,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset CompletedAt);

internal sealed record DispatchDto(string WorkerName, string Command, string WorkingDirectory, DateTimeOffset DispatchedAt);

internal sealed record ProcessDto(
    int ProcessId,
    string Command,
    bool IsRunning,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    bool WasCancelled,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath);

internal sealed record ProcessLogDto(
    string GoalId,
    int TaskNumber,
    string TaskId,
    int ProcessId,
    bool IsRunning,
    int? ExitCode,
    string StandardOutputPath,
    string StandardOutput,
    string StandardErrorPath,
    string StandardError,
    string ExitCodePath,
    string ExitCodeText);

internal sealed record TaskVerificationHistoryDto(
    string GoalId,
    int TaskNumber,
    string TaskId,
    IReadOnlyList<VerificationHistoryEntryDto> Verifications);

internal sealed record TaskVerificationPlanDto(
    string GoalId,
    int TaskNumber,
    string TaskId,
    string? Plan);

internal sealed record VerificationHistoryEntryDto(
    int Number,
    string Command,
    string WorkingDirectory,
    int ExitCode,
    bool Succeeded,
    string StandardOutput,
    string StandardError,
    DateTimeOffset CompletedAt);

internal sealed record TaskTimelineDto(
    string GoalId,
    int TaskNumber,
    string TaskId,
    IReadOnlyList<TimelineDto> Timeline);

internal sealed record VerificationDto(string Command, int ExitCode, bool Succeeded, DateTimeOffset CompletedAt, int HistoryCount);

internal sealed record TimelineDto(ProgressKind Kind, string Message, DateTimeOffset OccurredAt);
