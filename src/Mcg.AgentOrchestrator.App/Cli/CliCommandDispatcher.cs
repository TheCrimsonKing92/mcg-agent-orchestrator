using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandDispatcher
{
public static bool ExecuteCommand(
    IReadOnlyList<string> parts,
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    ref IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    ref WorkerProfileCatalog workerProfiles,
    ref Goal? currentGoal)
{
    var context = new CliExecutionContext(kernel, workspace, providers, agents, workerProfiles, currentGoal);
    var changed = CliCommandHandlers.Execute(parts, context);
    agents = context.Agents;
    workerProfiles = context.WorkerProfiles;
    currentGoal = context.CurrentGoal;
    return changed;
}
}
