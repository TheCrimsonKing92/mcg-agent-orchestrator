using Mcg.AgentOrchestrator.App.Orchestration;
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
    bool UsesComplexModel = false,
    int? CostGuardPromptCharacterCount = null);

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
    string RollbackSafety,
    string SubscriptionWorkers,
    IReadOnlyList<string> ApiSurfaces,
    IReadOnlyList<string> DashboardModes,
    IReadOnlyList<string> StateStores,
    IReadOnlyList<string> DistributedBoundaries,
    IReadOnlyList<string> SafetyGates,
    IReadOnlyList<string> RollbackProcedure,
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
            StatePath: workspace.StatePath,
            AgentCatalogPath: workspace.AgentCatalogPath,
            WorkerProfilePath: workspace.WorkerProfilePath,
            PromptDirectory: workspace.PromptDirectory,
            LogDirectory: workspace.LogDirectory,
            ContinuationStorePath: workspace.ContinuationStorePath,
            ExecutionDirectory: workspace.ExecutionDirectory,
            Persistence: "File state uses per-path in-process locks, atomic replace, and backup recovery from state.json.bak.",
            RollbackSafety: "Goal worktrees isolate file-touching work and acceptance fast-forwards or returns a manual merge command on divergence.",
            SubscriptionWorkers: "Subscription dispatches use worker profiles with role-based sandbox/permission placeholders and persisted continuation watches.",
            ApiSurfaces:
            [
                "/api/system/architecture reports tenant, storage, provider, worker, dashboard, and rollback topology.",
                "/api/system/dashboard-host reports bind URLs, restart command, tenant-scoped paths, and hosted source-survey links.",
                "/api/system/state-rollback restores state.json from state.json.bak in operator dashboard modes only.",
                "/api/source-survey?max=8 provides bounded repository discovery for distributed workers.",
                "/api/goals/{goalId}/work-summary and /api/tasks/{taskId}/work-summary provide compact handoff context.",
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
                $"Kernel state: {workspace.StatePath}",
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
                "State saves use per-file locks, temp files, atomic replacement, retry, and backup recovery.",
                "State rollback requires explicit CLI/API confirmation and archives the previous primary before restore.",
                "Subscription worker starts require explicit confirmation and paid-cost guard acknowledgements.",
                "Role-based sandbox and permission placeholders keep non-implementation roles read-only.",
                "Build/test cleanup exposes exact dashboard PIDs instead of broad process termination.",
            ],
            RollbackProcedure:
            [
                "Review the goal worktree diff before acceptance.",
                "Run repository verification from the worktree, independent of worker-reported status.",
                "Use acceptance to fast-forward the goal branch when main has not advanced.",
                "If main diverged, use the printed manual merge command instead of overwriting shared state.",
                "If state.json is corrupt, restart from state.json.bak and inspect the failed primary before continuing.",
                "Use state-rollback --confirm-state-rollback or POST /api/system/state-rollback?confirm=state-rollback to replace primary state from the backup.",
            ],
            ProviderCount: providerCount,
            AgentCount: agents.Count,
            WorkerProfileCount: workerProfiles.Profiles.Count,
            UsableWorkerProfileCount: usableProfiles,
            OperatorControlsEnabled: operatorControlsEnabled);
    }
}

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
