using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal enum WorkerProfileImportMode
{
    Merge,
    Replace
}

public sealed record ProviderSmokeReport(
    string Target,
    IReadOnlyList<ProviderSmokeReportEntry> Results,
    bool AnySucceeded,
    bool AnyConfigured);

public sealed record ProviderSmokeReportEntry(
    string ProviderName,
    string Status,
    string? ModelName,
    string Detail,
    string? StopReason,
    int? InputTokens,
    int? OutputTokens,
    string? ResponseText);

internal sealed record DistributedArchitectureReport(
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
    [property: JsonPropertyName("Dash" + "boardModes")] IReadOnlyList<string> HostModes,
    IReadOnlyList<string> StateStores,
    IReadOnlyList<string> DistributedBoundaries,
    IReadOnlyList<string> SafetyGates,
    int ProviderCount,
    int AgentCount,
    int WorkerProfileCount,
    int UsableWorkerProfileCount,
    bool OperatorControlsEnabled)
{
    public static DistributedArchitectureReport Create(
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

        return new DistributedArchitectureReport(
            workspace.TenantName,
            workspace.IsTenantScoped,
            workspace.SqliteStatePath,
            workspace.AgentCatalogPath,
            workspace.WorkerProfilePath,
            workspace.PromptDirectory,
            workspace.LogDirectory,
            workspace.ContinuationStorePath,
            workspace.ExecutionDirectory,
            "Kernel state is stored in SQLite state.db with WAL mode and transaction-scoped updates.",
            "Subscription dispatches use worker profiles with role-based sandbox/permission placeholders and persisted continuation watches.",
            [
                "/api/system/architecture reports tenant, storage, provider, worker, and dashboard topology.",
                "/api/system/dashboard-host reports bind URLs, restart command, tenant-scoped paths, and hosted source-survey links.",
                "/api/source-survey?max=8 provides bounded repository discovery for distributed workers.",
                "/api/goals/{goalId}/work-summary and /api/tasks/{taskId}/work-summary provide compact handoff context.",
                "/api/goals/{goalId}/events/stream provides a browser-native monitoring subscription with resumable timeline events.",
                "Operator POST endpoints remain disabled on simple-hosted-dashboard for read-only distributed access."
            ],
            [
                "serve-dashboard: local operator mode with write controls.",
                "hosted-dashboard: network operator mode with write controls.",
                "simple-hosted-dashboard: network read-only mode for distributed inspection.",
                "prototype-ui: isolated prototype workspace mode."
            ],
            [
                $"Kernel state: {workspace.SqliteStatePath}",
                $"Agent catalog: {workspace.AgentCatalogPath}",
                $"Worker profiles: {workspace.WorkerProfilePath}",
                $"Prompt handoffs: {workspace.PromptDirectory}",
                $"Worker logs: {workspace.LogDirectory}",
                $"Continuation watches: {workspace.ContinuationStorePath}"
            ],
            [
                $"Tenant '{workspace.TenantName}' owns an isolated orchestrator directory at {workspace.OrchestratorDirectory}.",
                $"File-touching goal work resolves through worktrees when present, otherwise {workspace.ExecutionDirectory}.",
                "API-key model execution stays in-process through provider registry calls.",
                "Subscription execution leaves process boundaries through worker profiles and persisted prompt/log paths.",
                "Optional web hosts expose read/write capability by mode instead of by endpoint convention alone."
            ],
            [
                "Tenant names are normalized and reject relative path segments.",
                "State updates use SQLite transactions with WAL durability.",
                "Subscription worker starts require explicit confirmation and paid-cost guard acknowledgements.",
                "Role-based sandbox and permission placeholders keep non-implementation roles read-only.",
                "Build/test cleanup exposes exact dashboard PIDs instead of broad process termination."
            ],
            providerCount,
            agents.Count,
            workerProfiles.Profiles.Count,
            usableProfiles,
            operatorControlsEnabled);
    }
}

internal static class ApplicationQueryJson
{
    private static readonly JsonSerializerOptions SharedOptions = CreateOptions();

    public static JsonSerializerOptions Options() => SharedOptions;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.WriteIndented = true;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record GoalMonitoringBatch(
    string GoalId,
    long SinceEventId,
    long LastEventId,
    GoalMonitoringSnapshot Snapshot,
    IReadOnlyList<GoalMonitoringEvent> Events,
    string StreamPath);

internal sealed record GoalMonitoringSnapshot(
    string GoalId,
    DateTimeOffset ObservedAt,
    long LastEventId,
    QueryMonitorSnapshot Monitor,
    IReadOnlyList<TaskMonitoringSnapshot> Tasks,
    QueryOperatorDisposition? OperatorDisposition = null,
    QueryOperatorInbox? OperatorInbox = null,
    QueryProviderCapacity? ProviderCapacity = null,
    string? GoalLabel = null);

internal sealed record QueryMonitorSnapshot(string StatusText, IReadOnlyList<QueryAttention> Attention);

internal sealed record QueryAttention;

internal sealed record QueryOperatorDisposition(
    OperatorDispositionState State,
    OperatorDispositionConfidence Confidence,
    string Reason,
    string NextSafeCommand,
    IReadOnlyList<string> Blockers);

internal sealed record QueryOperatorInbox(int OpenCount);

internal sealed record QueryProviderCapacity(
    ProviderCapacityDisposition Disposition,
    int ReadyNowCount,
    int DeferredCount);

internal sealed record TaskMonitoringSnapshot(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    WorkTaskStatus Status,
    QueryProcessSnapshot? LastProcess,
    DateTimeOffset? SubscriptionRetryAfter);

internal sealed record QueryProcessSnapshot(
    int ProcessId,
    string Command,
    bool IsRunning,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    int? ChildProcessId,
    int? ChildExitCode,
    bool WasCancelled,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath,
    double? HeartbeatAgeSeconds,
    double? HeartbeatIdleDurationSeconds,
    long? HeartbeatStdoutBytes,
    long? HeartbeatStderrBytes,
    string? HeartbeatPath,
    QueryDispatchHeartbeat Heartbeat);

internal sealed record QueryDispatchHeartbeat(
    bool IsAvailable,
    string State,
    DateTimeOffset? LastObservedAt);

internal sealed record GoalMonitoringEvent(
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

internal sealed record MonitorErrorEvent(string GoalId, string Code, string Message);

internal static class ApplicationMonitoringContract
{
    public const string KeepAliveEventName = "monitor.keepalive";
}

internal static class ApplicationGoalStatusText
{
    public static string Resolve(GoalStatus status, GoalLifecycleState? lifecycleState) =>
        lifecycleState switch
        {
            GoalLifecycleState.CleanedUp => nameof(GoalLifecycleState.CleanedUp),
            GoalLifecycleState.Merged or GoalLifecycleState.Recorded => "Landed",
            _ => Display(
                status == GoalStatus.Completed && lifecycleState != GoalLifecycleState.CleanedUp
                    ? GoalStatus.Verified
                    : status)
        };

    private static string Display(GoalStatus status) => status switch
    {
        GoalStatus.WaitingForHuman => "Waiting for human",
        GoalStatus.AcceptanceFailed => "Acceptance failed",
        _ => status.ToString()
    };
}
