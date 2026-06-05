using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record DashboardCommandArgs(string Path, DashboardRenderOptions Options);

internal sealed record DashboardHostArgs(
    string UrlPrefix,
    int? AutoRefreshSeconds,
    bool OpenBrowser = false,
    string CommandName = "serve-dashboard",
    string? PublicUrlPrefix = null,
    bool EnableOperatorControls = true);

internal sealed record GoalOperationPath(string GoalIdPrefix, string Operation, string? TaskIdPrefix, string? TaskOperation);

internal sealed record ProcessBatchExecutionResult(ProcessBatchPlan Plan, IReadOnlyList<TaskSpec> Tasks);

internal sealed record SubscriptionStartResult(IReadOnlyList<WorkerProfileDispatchResult> Dispatches, ProcessBatchExecutionResult Processes);


