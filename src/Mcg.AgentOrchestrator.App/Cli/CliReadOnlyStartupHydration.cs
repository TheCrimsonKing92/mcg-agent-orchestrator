using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadOnlyStartupHydration
{
    internal static async Task<(Goal? CurrentGoal, bool Hydrated)> PrepareStartupAsync(
        IReadOnlyList<string> args,
        IOrchestratorStateRepository stateRepository)
    {
        if (CliReadOnlyCommandRunner.IsReadOnlyCommand(args))
            return (null, false);

        var kernel = await stateRepository.LoadAsync();
        return (OrchestratorEntityResolver.GetLatestGoal(kernel), true);
    }

    internal static bool ExecuteStartupCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        bool hydrated,
        IOperatorChannel? channel = null,
        WorktreeCleanupContext? acceptanceCleanupContext = null,
        Action? onReadOnlyDeclined = null)
    {
        if (!hydrated)
        {
            if (CliReadOnlyCommandRunner.TryExecute(args, stateRepository, workspace, providers,
                channel, ref agents, ref workerProfiles, ref currentGoal, out var changed))
                return changed;

            onReadOnlyDeclined?.Invoke();
            currentGoal = OrchestratorEntityResolver.GetLatestGoal(
                stateRepository.LoadAsync().GetAwaiter().GetResult());
        }

        return CliPersistentStateRunner.ExecuteCommand(args, stateRepository, workspace, ref agents,
            providers, ref workerProfiles, ref currentGoal, channel,
            acceptanceCleanupContext: acceptanceCleanupContext,
            skipReadOnlyRoute: !hydrated);
    }
}
