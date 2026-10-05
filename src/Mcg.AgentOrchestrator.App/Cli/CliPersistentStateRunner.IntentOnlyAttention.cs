using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    internal static bool IsIntentOnlyAttentionCommand(IReadOnlyList<string> args) =>
        args.Count >= 2 &&
        args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        (args[1].Equals("answer", StringComparison.OrdinalIgnoreCase) ||
         args[1].Equals("dismiss", StringComparison.OrdinalIgnoreCase));

    private static bool ExecuteIntentOnlyAttentionCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var changed = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => stateRepository.LoadAsync().GetAwaiter().GetResult());

        if (changed)
            throw new InvalidOperationException(
                $"'{args[0]} {args[1]}' reported a state change on the intent-only route; nothing was saved.");

        return false;
    }
}
