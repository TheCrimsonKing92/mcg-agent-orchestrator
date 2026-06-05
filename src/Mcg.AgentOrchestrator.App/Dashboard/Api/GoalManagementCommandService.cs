using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
public static bool IsGoalBatchOperation(string operation)
{
    return operation.Equals("profile-dispatch-ready", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("subscription-dispatch-ready", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("start-subscription-ready", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("start-dispatches", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("refresh-dispatches", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("cancel-dispatches", StringComparison.OrdinalIgnoreCase);
}

public static BatchActionResultDto ApplyGoalBatchAction(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    Goal goal,
    string operation,
    string body,
    OrchestratorWorkspace workspace)
{
    return operation.ToLowerInvariant() switch
    {
        "profile-dispatch-ready" => ApplyProfileDispatchReady(kernel, workspace, goal, body),
        "subscription-dispatch-ready" => ApplySubscriptionDispatchReady(kernel, workspace, agents, goal),
        "start-subscription-ready" => ApplyStartSubscriptionReady(kernel, workspace, agents, goal),
        "start-dispatches" => DashboardResponseMapper.ToProcessBatchActionResultDto(goal, "start-dispatches", StartDispatches(kernel, workspace, goal)),
        "refresh-dispatches" => DashboardResponseMapper.ToProcessBatchActionResultDto(goal, "refresh-dispatches", RefreshDispatches(kernel, goal)),
        "cancel-dispatches" => DashboardResponseMapper.ToProcessBatchActionResultDto(goal, "cancel-dispatches", CancelDispatches(kernel, goal)),
        _ => throw new ArgumentException("Goal batch operation must be profile-dispatch-ready, subscription-dispatch-ready, start-subscription-ready, start-dispatches, refresh-dispatches, or cancel-dispatches.")
    };
}

public static BatchActionResultDto ApplyProfileDispatchReady(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, Goal goal, string body)
{
    var submission = DashboardRequestParser.ParseProfileDispatchReadySubmission(body);
    var profile = WorkerProfileStore.Load(workspace.WorkerProfilePath).GetRequired(submission.ProfileName);
    var results = ProfileDispatchReadyTasks(kernel, workspace, goal, profile);
    return DashboardResponseMapper.ToBatchActionResultDto(goal, "profile-dispatch-ready", results.Select(result => result.Task).ToList(), results);
}

public static BatchActionResultDto ApplySubscriptionDispatchReady(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, IReadOnlyList<AgentDefinition> agents, Goal goal)
{
    var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var results = SubscriptionDispatchReadyTasks(kernel, workspace, goal, agents, profiles);
    return DashboardResponseMapper.ToBatchActionResultDto(goal, "subscription-dispatch-ready", results.Select(result => result.Task).ToList(), results);
}

public static BatchActionResultDto ApplyStartSubscriptionReady(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, IReadOnlyList<AgentDefinition> agents, Goal goal)
{
    var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var result = StartSubscriptionReadyTasks(kernel, workspace, goal, agents, profiles);
    return DashboardResponseMapper.ToSubscriptionStartActionResultDto(goal, result);
}

}


