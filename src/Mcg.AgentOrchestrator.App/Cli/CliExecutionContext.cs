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
    Func<AgentOrchestratorKernel>? reloadKernel = null)
{
public AgentOrchestratorKernel Kernel { get; } = kernel;

public AgentOrchestratorKernel ReloadKernel() => reloadKernel?.Invoke() ?? Kernel;

public OrchestratorWorkspace Workspace { get; } = workspace;

public string StatePath => Workspace.StatePath;

public string AgentCatalogPath => Workspace.AgentCatalogPath;

public IModelProviderRegistry Providers { get; } = providers;

public string WorkerProfilePath => Workspace.WorkerProfilePath;

public IReadOnlyList<AgentDefinition> Agents { get; set; } = agents;

public WorkerProfileCatalog WorkerProfiles { get; set; } = workerProfiles;

public Goal? CurrentGoal { get; set; } = currentGoal;

public IGoalAcceptanceVerifier AcceptanceVerifier { get; init; } = new GoalAcceptanceVerifier();

public IOperatorChannel Channel { get; } = channel ?? NullOperatorChannel.Instance;
}
