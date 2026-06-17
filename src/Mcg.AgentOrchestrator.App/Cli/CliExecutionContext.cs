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
    IOperatorChannel? channel = null)
{
public AgentOrchestratorKernel Kernel { get; } = kernel;

public OrchestratorWorkspace Workspace { get; } = workspace;

public string StatePath => Workspace.StatePath;

public string AgentCatalogPath => Workspace.AgentCatalogPath;

public IModelProviderRegistry Providers { get; } = providers;

public string WorkerProfilePath => Workspace.WorkerProfilePath;

public IReadOnlyList<AgentDefinition> Agents { get; set; } = agents;

public WorkerProfileCatalog WorkerProfiles { get; set; } = workerProfiles;

public Goal? CurrentGoal { get; set; } = currentGoal;

public GoalAcceptanceVerifier AcceptanceVerifier { get; init; } = new();

public IOperatorChannel Channel { get; } = channel ?? NullOperatorChannel.Instance;
}
