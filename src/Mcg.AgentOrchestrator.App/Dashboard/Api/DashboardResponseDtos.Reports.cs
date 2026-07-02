using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record StatusCountDto(WorkTaskStatus Status, int Count);

internal sealed record AttentionDto(TaskAttentionKind Kind, string? TaskId, string Message);

internal sealed record HumanInputDto(
    string Id,
    string WaitId,
    string GoalId,
    string? TaskId,
    int? TaskNumber,
    string Question,
    DateTimeOffset RequestedAt,
    HumanWaitKind Kind,
    bool IsAutoDefaultable,
    bool IsDismissible,
    bool IsAnswerRequired,
    bool IsExternallyBlocked,
    long AgeSeconds,
    string ResumeCommand,
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
    string WaitId,
    string? TaskId,
    int? TaskNumber,
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
    string GoalId,
    string ResumeCommand,
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

internal sealed record GoalSupervisorPlanDto(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    bool ApplySafe,
    IReadOnlyList<string> AppliedActions,
    IReadOnlyList<GoalSupervisorProposalDto> Proposals);

internal sealed record GoalSupervisorProposalDto(
    GoalSupervisorProposalKind Kind,
    int? TaskNumber,
    string? TaskId,
    AutonomyAction? PolicyAction,
    bool PolicyAllows,
    bool CanApply,
    bool RequiresOperatorGate,
    string Reason,
    string SuggestedCommand);

internal sealed record FailureTriageReportDto(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    IReadOnlyList<FailureTriageItemDto> Items);

internal sealed record FailureTriageItemDto(
    int? TaskNumber,
    string? TaskId,
    FailureTriageCause Cause,
    FailureTriageAction Action,
    AutonomyAction? PolicyAction,
    bool PolicyAllows,
    bool CanAutoApply,
    bool RequiresOperatorGate,
    string Explanation,
    string SuggestedCommand);

internal sealed record DashboardActionRecommendationReportDto(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    DashboardActionRecommendationDto? Primary,
    IReadOnlyList<DashboardActionRecommendationDto> Secondary,
    IReadOnlyList<string> SourceSummaries);

internal sealed record DashboardActionRecommendationDto(
    DashboardActionRecommendationSource Source,
    string Title,
    string Reason,
    string SuggestedCommand,
    string? ApiMethod,
    string? ApiPath,
    bool CanApply,
    bool RequiresOperatorGate);

public sealed record OperatorInboxReportDto(
    string? GoalPrefix,
    int TotalCount,
    int OpenCount,
    int AcknowledgedCount,
    IReadOnlyList<OperatorInboxItemDto> Items);

public sealed record OperatorInboxItemDto(
    string Id,
    OperatorInboxKind Kind,
    OperatorInboxSeverity Severity,
    string GoalId,
    string GoalPrefix,
    string Objective,
    string? TaskId,
    int? TaskNumber,
    string Title,
    string Message,
    string Evidence,
    string SuggestedAction,
    string SuggestedCommand,
    string Source,
    bool Acknowledged,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgementNote);

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
    IReadOnlyList<ModelFitSummaryDto> ModelFit,
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
    int? PromptCharacterCount = null,
    bool UsesComplexModel = false);

internal sealed record ModelFitSummaryDto(
    string ProviderName,
    string ModelName,
    int NoteCount,
    int AdequateCount,
    int OverkillCount,
    int UnderpoweredCount,
    int UnknownCount,
    IReadOnlyList<string>? TaskShapes = null);

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
    string Message,
    string? ModelFitNote = null);

internal sealed record GoalWorkSummaryDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    int PendingHumanInputCount,
    bool VerificationSatisfied,
    NextActionDto? NextAction,
    DashboardHostInfoDto? Host,
    GoalBuildEnvironmentDto BuildEnvironment,
    IReadOnlyList<TaskWorkSummaryDto> Tasks,
    string? MonitoringStreamPath = null,
    ParallelExecutionPlanDto? ParallelPlan = null,
    GoalTestImpactDto? TestImpact = null);

internal sealed record GoalTestImpactDto(
    bool RequiresBuild,
    bool RequiresBroadVerification,
    string Summary,
    IReadOnlyList<GoalTestImpactCheckDto> Checks);

internal sealed record GoalTestImpactCheckDto(
    string Name,
    string CommandLine,
    string Reason);

internal sealed record GoalBuildEnvironmentDto(
    string LeaseId,
    string RootPath,
    string ArtifactsPath,
    string LeaseMetadataPath,
    bool LeaseExists);

internal sealed record BacklogGoalPlanDto(
    string BacklogPath,
    int NodeCount,
    int EdgeCount,
    CompiledGoalGraphDto CompiledGraph,
    ParallelExecutionPlanDto ParallelPlan);

internal sealed record CrossGoalStartPlanDto(
    int CandidateCount,
    IReadOnlyList<CrossGoalStartCandidateDto> Candidates,
    ParallelExecutionPlanDto ParallelPlan,
    GoalDrainPolicyDto? DrainPolicy = null);

internal sealed record GoalDrainPolicyDto(
    string Name,
    int MaxSubscriptionStartsPerDrain,
    IReadOnlyList<string> AllowedRoles,
    IReadOnlyList<string> AllowedProviders,
    string LargePromptBehavior,
    bool RequireReadinessRiskConfirmation,
    bool RequireAcceptanceGate,
    IReadOnlyList<string> AllowedLocalTimeWindows);

internal sealed record CrossGoalStartCandidateDto(
    string GoalId,
    string GoalPrefix,
    string Objective,
    IReadOnlyList<int> TaskNumbers,
    IReadOnlyList<string> TargetPaths,
    IReadOnlyList<string> RequiredResources,
    string? ProviderKey,
    bool RequiresCostConfirmation,
    string Detail);

internal sealed record CompiledGoalGraphDto(
    string GraphId,
    bool IsRunnable,
    IReadOnlyList<CompiledGoalNodeDto> Nodes,
    IReadOnlyList<CompiledGoalEdgeDto> Edges,
    IReadOnlyList<CompiledGoalValidationFindingDto> Findings);

internal sealed record CompiledGoalNodeDto(
    string Id,
    string Heading,
    IReadOnlyList<string> FileScopes,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> VerificationContracts,
    string RollbackBoundary,
    int? ParallelBatch,
    ParallelExecutionDisposition ParallelDisposition,
    bool CanCreateGoal);

internal sealed record CompiledGoalEdgeDto(string FromId, string ToId, string Reason);

internal sealed record CompiledGoalValidationFindingDto(string Severity, string NodeId, string Message);

internal sealed record GoalMonitoringBatchDto(
    string GoalId,
    long SinceEventId,
    long LastEventId,
    GoalMonitoringSnapshotDto Snapshot,
    IReadOnlyList<GoalMonitoringEventDto> Events,
    string StreamPath);

internal sealed record GoalMonitoringSnapshotDto(
    string GoalId,
    DateTimeOffset ObservedAt,
    long LastEventId,
    MonitorDto Monitor,
    IReadOnlyList<TaskMonitoringSnapshotDto> Tasks,
    OperatorInboxReportDto? OperatorInbox = null,
    ProviderCapacityScheduleDto? ProviderCapacity = null,
    string? GoalLabel = null);

internal sealed record TaskMonitoringSnapshotDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    ProcessDto? LastProcess,
    DateTimeOffset? SubscriptionRetryAfter);

internal sealed record TaskStatusMonitoringEventDto(
    string GoalId,
    string TaskId,
    int TaskNumber,
    AgentRole Role,
    WorkTaskStatus Status,
    long TimelineEventId,
    DateTimeOffset OccurredAt);

internal sealed record GoalMonitoringEventDto(
    long Id,
    string Event,
    string GoalId,
    string? TaskId,
    int? TaskNumber,
    AgentRole? Role,
    WorkTaskStatus? TaskStatus,
    ProgressKind Kind,
    string Message,
    bool MessageTruncated,
    int MessageLength,
    DateTimeOffset OccurredAt);

internal sealed record MonitorErrorEventDto(string GoalId, string Code, string Message);

internal sealed record ConductorProgressLineEventDto(string Line);

internal sealed record TaskWorkContextDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int TotalTasks,
    int PendingHumanInputCount,
    bool VerificationSatisfied,
    NextActionDto? NextAction,
    TaskWorkSummaryDto Task,
    DashboardHostInfoDto Host);

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
    int? PromptCharacterCount = null,
    bool UsesComplexModel = false);

internal sealed record ProcessSummaryDto(
    int ProcessId,
    bool IsRunning,
    int? ExitCode,
    bool WasCancelled,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath,
    DispatchHeartbeatDto Heartbeat);

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
    NextActionControlDto? Control,
    DispatchRecoveryDecisionDto? Recovery);

internal sealed record DispatchRecoveryDecisionDto(
    DispatchRecoveryAction Action,
    string ActionName,
    string EvidencePath,
    string Reason,
    string? Blocker);

internal sealed record NextActionControlDto(
    string Label,
    string Method,
    string Url,
    string? CostRisk = null,
    string? CostRecommendation = null);
