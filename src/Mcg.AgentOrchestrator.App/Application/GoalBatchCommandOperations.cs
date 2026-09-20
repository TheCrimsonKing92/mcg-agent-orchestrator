using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Application boundary for goal-wide dispatch mutations requested by a transport adapter.
/// </summary>
public sealed class GoalBatchCommandOperations
{
    private readonly GoalDispatchOperations _dispatch = new();

    public IReadOnlyList<WorkerProfileDispatchResult> ProfileDispatchReadyTasks(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        WorkerProfile profile,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry? providers = null) =>
        _dispatch.ProfileDispatchReadyTasks(kernel, workspace, goal, profile, agents, providers);

    public WorkerProfileReadyBatchResult SubscriptionDispatchReadyBatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers = null) =>
        _dispatch.SubscriptionDispatchReadyBatch(kernel, workspace, goal, agents, profiles, providers);

    public SubscriptionStartResult StartSubscriptionReadyTasks(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers = null) =>
        _dispatch.StartSubscriptionReadyTasks(kernel, workspace, goal, agents, profiles, providers);

    public ProcessBatchExecutionResult StartDispatches(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal) =>
        _dispatch.StartDispatches(kernel, workspace, goal);

    public ProcessBatchExecutionResult RefreshDispatches(AgentOrchestratorKernel kernel, Goal goal) =>
        _dispatch.RefreshDispatches(kernel, goal);

    public ProcessBatchExecutionResult CancelDispatches(AgentOrchestratorKernel kernel, Goal goal) =>
        _dispatch.CancelDispatches(kernel, goal);
}
