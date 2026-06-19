using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class CliExecutionContext(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry providers,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog workerProfiles,
    Goal? currentGoal,
    IOperatorChannel? channel = null,
    Func<AgentOrchestratorKernel>? reloadKernel = null,
    Action<AgentOrchestratorKernel>? persistKernel = null)
{
public AgentOrchestratorKernel Kernel { get; } = kernel;

public AgentOrchestratorKernel ReloadKernel() => reloadKernel?.Invoke() ?? Kernel;

/// <summary>
/// Durably commits the current kernel state mid-command. Long-running loops (conduct --loop/--watch)
/// run outside the single wrapping state transaction and call this per tick so each tick's progress
/// survives a reload or a killed process. No-op for ordinary commands (which commit on return).
/// </summary>
public void PersistCheckpoint(AgentOrchestratorKernel checkpointKernel) => persistKernel?.Invoke(checkpointKernel);

public OrchestratorWorkspace Workspace { get; } = workspace;

public string AgentCatalogPath => Workspace.AgentCatalogPath;

public IModelProviderRegistry Providers { get; } = providers;

public string WorkerProfilePath => Workspace.WorkerProfilePath;

public IReadOnlyList<AgentDefinition> Agents { get; set; } = agents;

public WorkerProfileCatalog WorkerProfiles { get; set; } = workerProfiles;

public Goal? CurrentGoal { get; set; } = currentGoal;

public IGoalAcceptanceVerifier AcceptanceVerifier { get; init; } = new GoalAcceptanceVerifier();

public IOperatorChannel Channel { get; } = channel ?? NullOperatorChannel.Instance;
}
