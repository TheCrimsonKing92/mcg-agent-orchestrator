using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadOnlyQueryExecutor
{
    internal static void Execute(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IClock? diagnosticsClock = null)
    {
        var context = new CliExecutionContext(kernel, workspace, providers, agents, workerProfiles,
            currentGoal, channel) { IsReadOnlyQuery = true, DiagnosticsClock = diagnosticsClock };
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (CliCommandHandlers.Execute(args, context))
                throw new InvalidOperationException($"{args[0]} attempted to mutate state through its read-only route.");
        }
        finally
        {
            agents = context.Agents;
            workerProfiles = context.WorkerProfiles;
            currentGoal = context.CurrentGoal;
        }
    }
}
