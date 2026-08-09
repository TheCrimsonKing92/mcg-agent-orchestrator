using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
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
    ProviderCapacityScheduleDto CapacitySchedule,
    IReadOnlyList<SubscriptionPlanModelSummaryDto> ReadyModelUsage,
    IReadOnlyList<SubscriptionProviderBudgetSummaryDto> ProviderBudgets,
    IReadOnlyList<SubscriptionPlanItemDto> Items);

internal sealed record SubscriptionProviderBudgetSummaryDto(
    string ProviderName,
    int TaskCount,
    int ReadyCount,
    int DeferredCount,
    int RecoverableLimitFailureCount,
    bool IsCoolingDown,
    DateTimeOffset? RetryAfter,
    int? RetryDelaySeconds,
    int? SourceTaskNumber,
    string Detail);

internal sealed record ProviderCapacityScheduleDto(
    ProviderCapacityDisposition Disposition,
    string Recommendation,
    int ReadyNowCount,
    int DeferredCount,
    DateTimeOffset? NextRetryAfter,
    bool HasCostRisk,
    IReadOnlyList<ProviderCapacityActionDto> Actions);

internal sealed record ProviderCapacityActionDto(
    int TaskNumber,
    string TaskId,
    string? ProviderName,
    ProviderCapacityDisposition Disposition,
    DateTimeOffset? RetryAfter,
    string Recommendation,
    IReadOnlyList<string> Alternatives);

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

internal sealed record WorkerRouteDecisionDto(
    WorkerRouteDisposition Disposition,
    string Recommendation,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Alternatives);

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
    bool UsesComplexModel = false,
    int? CostGuardPromptCharacterCount = null,
    WorkerRouteDecisionDto? Route = null);

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

public sealed record TaskDurationStatsDto(
    string Scope,
    AgentRole Role,
    TaskComplexity Complexity,
    string? ProviderName,
    string? ModelName,
    int TaskCount,
    int AttemptCount,
    int FailedAttemptCount,
    int MinimumSampleCount,
    bool HasPublishedStats,
    string MedianLegitimateRuntime,
    string P90LegitimateRuntime,
    string MedianFailureInterventionOverhead,
    double FailureRate,
    int RealFailureAttemptCount,
    int EnvironmentalFailureAttemptCount,
    int ManufacturedFixedFailureAttemptCount,
    int UnknownEraFailureAttemptCount,
    double RealFailureRate,
    double EnvironmentalFailureRate,
    double ManufacturedFixedFailureRate,
    double UnknownEraFailureRate);

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
    string RestartCommand,
    string TenantName,
    bool TenantScoped,
    string StatePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string ContinuationStorePath);

internal sealed record DistributedArchitectureDto(
    string TenantName,
    bool TenantScoped,
    string StatePath,
    string AgentCatalogPath,
    string WorkerProfilePath,
    string PromptDirectory,
    string LogDirectory,
    string ContinuationStorePath,
    string ExecutionDirectory,
    string Persistence,
    string SubscriptionWorkers,
    IReadOnlyList<string> ApiSurfaces,
    IReadOnlyList<string> DashboardModes,
    IReadOnlyList<string> StateStores,
    IReadOnlyList<string> DistributedBoundaries,
    IReadOnlyList<string> SafetyGates,
    int ProviderCount,
    int AgentCount,
    int WorkerProfileCount,
    int UsableWorkerProfileCount,
    bool OperatorControlsEnabled)
{
    public static DistributedArchitectureDto Create(
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        bool operatorControlsEnabled)
    {
        var usableProfiles = workerProfiles.Profiles.Count(profile =>
            !string.IsNullOrWhiteSpace(profile.CommandTemplate) &&
            !WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate));
        var providerCount = agents
            .Select(agent => agent.Model.ProviderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new DistributedArchitectureDto(
            TenantName: workspace.TenantName,
            TenantScoped: workspace.IsTenantScoped,
            StatePath: workspace.SqliteStatePath,
            AgentCatalogPath: workspace.AgentCatalogPath,
            WorkerProfilePath: workspace.WorkerProfilePath,
            PromptDirectory: workspace.PromptDirectory,
            LogDirectory: workspace.LogDirectory,
            ContinuationStorePath: workspace.ContinuationStorePath,
            ExecutionDirectory: workspace.ExecutionDirectory,
            Persistence: "Kernel state is stored in SQLite state.db with WAL mode and transaction-scoped updates.",
            SubscriptionWorkers: "Subscription dispatches use worker profiles with role-based sandbox/permission placeholders and persisted continuation watches.",
            ApiSurfaces:
            [
                "/api/system/architecture reports tenant, storage, provider, worker, and dashboard topology.",
                "/api/system/dashboard-host reports bind URLs, restart command, tenant-scoped paths, and hosted source-survey links.",
                "/api/source-survey?max=8 provides bounded repository discovery for distributed workers.",
                "/api/goals/{goalId}/work-summary and /api/tasks/{taskId}/work-summary provide compact handoff context.",
                "/api/goals/{goalId}/events/stream provides a browser-native monitoring subscription with resumable timeline events.",
                "Operator POST endpoints remain disabled on simple-hosted-dashboard for read-only distributed access.",
            ],
            DashboardModes:
            [
                "serve-dashboard: local operator mode with write controls.",
                "hosted-dashboard: network operator mode with write controls.",
                "simple-hosted-dashboard: network read-only mode for distributed inspection.",
                "prototype-ui: isolated prototype workspace mode.",
            ],
            StateStores:
            [
                $"Kernel state: {workspace.SqliteStatePath}",
                $"Agent catalog: {workspace.AgentCatalogPath}",
                $"Worker profiles: {workspace.WorkerProfilePath}",
                $"Prompt handoffs: {workspace.PromptDirectory}",
                $"Worker logs: {workspace.LogDirectory}",
                $"Continuation watches: {workspace.ContinuationStorePath}",
            ],
            DistributedBoundaries:
            [
                $"Tenant '{workspace.TenantName}' owns an isolated orchestrator directory at {workspace.OrchestratorDirectory}.",
                $"File-touching goal work resolves through worktrees when present, otherwise {workspace.ExecutionDirectory}.",
                "API-key model execution stays in-process through provider registry calls.",
                "Subscription execution leaves process boundaries through worker profiles and persisted prompt/log paths.",
                "Dashboard hosts expose read/write capability by mode instead of by endpoint convention alone.",
            ],
            SafetyGates:
            [
                "Tenant names are normalized and reject relative path segments.",
                "State updates use SQLite transactions with WAL durability.",
                "Subscription worker starts require explicit confirmation and paid-cost guard acknowledgements.",
                "Role-based sandbox and permission placeholders keep non-implementation roles read-only.",
                "Build/test cleanup exposes exact dashboard PIDs instead of broad process termination.",
            ],
            ProviderCount: providerCount,
            AgentCount: agents.Count,
            WorkerProfileCount: workerProfiles.Profiles.Count,
            UsableWorkerProfileCount: usableProfiles,
            OperatorControlsEnabled: operatorControlsEnabled);
    }
}

internal sealed record GoalSummaryDto(string Id, string Objective, GoalStatus Status, int TotalTasks, DateTimeOffset? LastEventAt)
{
    public string StatusText { get; init; } = DashboardDisplayNames.Display(Status);
    public string? Condition { get; init; }
}

internal sealed record GoalDetailDto(
    GoalSummaryDto Goal,
    IReadOnlyList<TaskSummaryDto> Tasks,
    bool VerificationSatisfied,
    AdvanceLoopResultDto? AutoHandoff = null,
    string? MonitoringStreamPath = null);

internal sealed record DelegationPlanDto(string GoalId, IReadOnlyList<TaskAssignmentDto> Assignments);

internal sealed record TaskAssignmentDto(string TaskId, string AgentId, AgentRole Role);

internal enum WorkerProfileImportMode
{
    Merge,
    Replace
}
