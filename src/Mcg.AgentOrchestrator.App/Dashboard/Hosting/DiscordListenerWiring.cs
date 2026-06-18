using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Hosting;

internal static class DiscordListenerWiring
{
    // Production: full wiring — split command string, run through CliCommandDispatcher,
    // acknowledge inbox item in the acks file, resolve collaboration item.
    public static DiscordDecisionApplier BuildApplier(
        OrchestratorWorkspace workspace,
        IOrchestratorStateRepository repository,
        IModelProviderRegistry providers,
        AgentCatalog? agentCatalogFallback)
    {
        return new DiscordDecisionApplier(
            workspace.OrchestratorDirectory,
            dispatch: (cmd, ct) => RunCliCommandAsync(
                CliArgumentParser.SplitCommand(cmd), ct, workspace, repository, providers, agentCatalogFallback),
            acknowledge: itemId => OperatorInbox.AppendAcknowledgement(workspace, itemId),
            collaborationStore: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    // Testable overload: accepts a seam in place of the real CliCommandDispatcher call.
    // The command string is still split via CliArgumentParser.SplitCommand before the seam
    // is invoked, so tests can assert on the parsed parts.
    public static DiscordDecisionApplier BuildApplier(
        OrchestratorWorkspace workspace,
        Func<IReadOnlyList<string>, CancellationToken, Task> dispatchSeam)
    {
        return new DiscordDecisionApplier(
            workspace.OrchestratorDirectory,
            dispatch: (cmd, ct) => dispatchSeam(CliArgumentParser.SplitCommand(cmd), ct),
            acknowledge: itemId => OperatorInbox.AppendAcknowledgement(workspace, itemId),
            collaborationStore: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    private static async Task RunCliCommandAsync(
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken,
        OrchestratorWorkspace workspace,
        IOrchestratorStateRepository repository,
        IModelProviderRegistry providers,
        AgentCatalog? agentCatalogFallback)
    {
        var kernel = await repository.LoadAsync(cancellationToken);
        var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentCatalogFallback).Agents;
        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        Goal? currentGoal = OrchestratorEntityResolver.GetLatestGoal(kernel);

        var changed = CliCommandDispatcher.ExecuteCommand(
            parts, kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        if (changed)
            await repository.SaveAsync(kernel, cancellationToken);
    }
}
