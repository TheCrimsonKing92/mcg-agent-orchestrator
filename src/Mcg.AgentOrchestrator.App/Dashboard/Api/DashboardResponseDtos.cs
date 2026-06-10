using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record AgentDto(
    string Id,
    string Name,
    AgentRole Role,
    string ProviderName,
    string ModelName,
    string? ReasoningEffort,
    int? MaxOutputTokens,
    AgentStatus Status,
    AgentExecutionPolicy ExecutionPolicy,
    string? SubscriptionProfileName,
    string? SubscriptionModelAlias,
    string? SubscriptionReasoningEffort,
    string? ComplexProviderName,
    string? ComplexModelName,
    int? ComplexMaxOutputTokens,
    string? ComplexReasoningEffort);

internal sealed record WorkerProfileDto(
    string Name,
    string CommandTemplate,
    string Executable,
    bool IsResolvable,
    bool IsEchoOnly,
    bool IsPatchCapable,
    bool IsOptional,
    string Detail);

internal sealed record SubscriptionPlanDto(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int ReadyToPrepareCount,
    int ResolvableProfileCount,
    int RetryDeferredCount,
    DateTimeOffset? NextSubscriptionRetryAfter,
    string? ReadyStartCostRisk,
    int? ReadyStartPromptCharacterCount,
    IReadOnlyList<string> ReadyStartCostRiskDetails,
    string? ReadyStartCostRecommendation,
    IReadOnlyList<SubscriptionPlanModelSummaryDto> ReadyModelUsage,
    IReadOnlyList<SubscriptionPlanItemDto> Items);

internal sealed record SubscriptionPlanModelSummaryDto(
    string ProviderName,
    string ModelName,
    int ReadyCount,
    TaskComplexity? TaskComplexity = null,
    string? ReasoningEffort = null,
    bool IsPotentiallyPaidProvider = false,
    int? EstimatedPromptCharacterCount = null,
    int PreviousModelFitNoteCount = 0,
    int PreviousAdequateCount = 0,
    int PreviousOverkillCount = 0,
    int PreviousUnderpoweredCount = 0,
    int PreviousUnknownFitCount = 0,
    IReadOnlyList<string>? PreviousTaskShapes = null,
    string? ModelFitRecommendation = null,
    bool UsesComplexModel = false);

internal sealed record SubscriptionPlanItemDto(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    WorkTaskStatus TaskStatus,
    string Description,
    string? AgentId,
    string? AgentName,
    string? ProviderName,
    string? ModelName,
    AgentExecutionPolicy? ExecutionPolicy,
    string? ProfileName,
    string? SubscriptionModelAlias,
    bool ProfileExists,
    bool ProfileIsResolvable,
    bool ProfileIsEchoOnly,
    bool ProfileIsPatchCapable,
    bool CanPrepare,
    string Detail,
    DateTimeOffset? RetryAfter = null,
    int? RetryDelaySeconds = null,
    TaskComplexity? TaskComplexity = null,
    string? SubscriptionModelName = null,
    string? SubscriptionReasoningEffort = null,
    int? EstimatedPromptCharacterCount = null,
    int RecoverableSubscriptionLimitFailureCount = 0,
    bool UsesComplexModel = false);

internal sealed record ProviderSmokeReportDto(
    string Target,
    IReadOnlyList<ProviderSmokeResultDto> Results,
    bool AnySucceeded,
    bool AnyConfigured);

internal sealed record ProviderSmokeResultDto(
    string ProviderName,
    string Status,
    string? ModelName,
    string Detail,
    string? StopReason,
    int? InputTokens,
    int? OutputTokens,
    string? ResponseText);

internal sealed record DashboardStopResultDto(
    int ProcessId,
    IReadOnlyList<int> ListeningPorts,
    IReadOnlyList<DashboardStopSiblingDto> SiblingProcesses,
    string RestartCommand,
    string Message);

internal sealed record DashboardStopSiblingDto(
    int ProcessId,
    IReadOnlyList<int> ListeningPorts,
    string SafeStopCommand);

internal sealed record DashboardBuildTestCleanupDto(
    int CurrentProcessId,
    IReadOnlyList<int> CurrentListeningPorts,
    string StopCurrentUrl,
    string RunBuildTestCycleUrl,
    IReadOnlyList<DashboardStopSiblingDto> SiblingProcesses,
    string VerifyNoAppProcessesCommand,
    string BuildCommand,
    string TestCommand,
    string RestartCommand,
    string BuildTestCycleCommand,
    IReadOnlyList<string> Checklist);

internal sealed record DashboardBuildTestRunDto(
    int ProcessId,
    string Command,
    string RunnerPath,
    string OutputLogPath,
    string ErrorLogPath,
    string Message);

public sealed record DashboardBuildTestRunSummaryDto(
    string Stamp,
    DateTimeOffset? StartedAt,
    bool HasOutputLog,
    bool HasErrorLog,
    bool BuildSucceeded,
    bool TestSucceeded,
    string Status,
    string RunnerPath,
    string OutputLogPath,
    string ErrorLogPath,
    string OutputPreview,
    string ErrorPreview);

internal sealed record DashboardHostInfoDto(
    string CommandName,
    string BindUrl,
    string BrowserUrl,
    string DashboardUrl,
    IReadOnlyList<string> HostedDashboardUrls,
    string SourceSurveyUrl,
    IReadOnlyList<string> HostedSourceSurveyUrls,
    string HostedAccessNote,
    int? AutoRefreshSeconds,
    bool OpensBrowser,
    bool OperatorControlsEnabled,
    string RestartCommand);

internal sealed record GoalSummaryDto(string Id, string Objective, GoalStatus Status, int TotalTasks, DateTimeOffset? LastEventAt);

internal sealed record GoalDetailDto(
    GoalSummaryDto Goal,
    IReadOnlyList<TaskSummaryDto> Tasks,
    bool VerificationSatisfied,
    AdvanceLoopResultDto? AutoHandoff = null);

internal sealed record DelegationPlanDto(string GoalId, IReadOnlyList<TaskAssignmentDto> Assignments);

internal sealed record TaskAssignmentDto(string TaskId, string AgentId, AgentRole Role);

internal enum WorkerProfileImportMode
{
    Merge,
    Replace
}
