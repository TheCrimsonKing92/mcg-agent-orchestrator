using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record ProfileDispatchDto(TaskSummaryDto Task, string PromptPath, DispatchDto? LastDispatch);

internal sealed record BatchActionResultDto(
    string GoalId,
    string Action,
    int Count,
    IReadOnlyList<TaskDetailDto> Tasks,
    IReadOnlyList<ProfileDispatchDto>? Dispatches = null,
    ProcessBatchPlanDto? ProcessPlan = null,
    IReadOnlyList<ProcessBatchOutcomeDto>? Processes = null,
    ParallelExecutionPlanDto? ParallelPlan = null);

internal sealed record ProcessBatchPlanDto(
    ProcessBatchActionKind Action,
    int ReadyCount,
    int SkippedCount,
    IReadOnlyList<ProcessBatchPlanItemDto> Items);

internal sealed record ProcessBatchPlanItemDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    string Description,
    bool DescriptionTruncated,
    int DescriptionLength,
    WorkTaskStatus TaskStatus,
    ProcessBatchItemStatus Status,
    string Reason);

internal sealed record ProcessBatchOutcomeDto(int TaskNumber, string TaskId, ProcessDto? Process);

internal sealed record ParallelExecutionPlanDto(
    IReadOnlyList<ParallelExecutionBatchDto> Batches,
    IReadOnlyList<ParallelExecutionDecisionDto> Decisions);

internal sealed record ParallelExecutionBatchDto(int Number, IReadOnlyList<string> IntentIds);

internal sealed record ParallelExecutionDecisionDto(
    string IntentId,
    ParallelExecutionDisposition Disposition,
    int? BatchNumber,
    IReadOnlyList<string> Reasons);

internal sealed record AdvanceResultDto(
    string GoalId,
    bool Executed,
    NextActionDto? Action,
    NextActionAutomationKind AutomationKind,
    string Message,
    object? Result);

internal sealed record AdvanceLoopResultDto(
    string GoalId,
    bool Executed,
    int StepCount,
    string StopReason,
    NextActionDto? BlockingAction,
    IReadOnlyList<AdvanceResultDto> Steps,
    DashboardContinuationStatusDto? Continuation = null,
    DateTimeOffset? ContinueAfter = null);

public sealed record DashboardContinuationStatusDto(
    string GoalId,
    bool IsRunning,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastCheckedAt,
    int IterationCount,
    string StopReason,
    string? LastError,
    DateTimeOffset? NextCheckAt = null,
    bool RestoredFromStore = false);

public sealed record DashboardContinuationSummaryDto(
    int Total,
    int Running,
    int Stopped,
    int Failed,
    int Restored,
    int CurrentProcess,
    int RetryDeferred,
    int WaitingForBackgroundWork,
    int MonitoringOnly,
    DateTimeOffset? NextCheckAt,
    IReadOnlyList<string> RunningGoalPrefixes);
