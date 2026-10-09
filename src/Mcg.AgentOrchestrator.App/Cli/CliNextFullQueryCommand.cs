using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliNextFullQueryCommand
{
    internal static bool IsNextPrefixQueryCommand(IReadOnlyList<string> args) =>
        (args.Count == 1 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase)) ||
        (args.Count == 2 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase) &&
         !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith("-", StringComparison.Ordinal));

    internal static bool IsNextFullQueryCommand(IReadOnlyList<string> args) =>
        ((args.Count == 3 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase) &&
          args[1].Equals("--full", StringComparison.OrdinalIgnoreCase) &&
          !string.IsNullOrWhiteSpace(args[2]) && !args[2].StartsWith("-", StringComparison.Ordinal)) ||
         (args.Count == 2 && args[0].Equals("goal-diagnostics", StringComparison.OrdinalIgnoreCase) &&
          !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith("-", StringComparison.Ordinal)) ||
         IsNextPrefixQueryCommand(args)) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static bool TryExecute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IClock? diagnosticsClock = null)
    {
        if (args.Count == 1)
            args = [args[0], CliCurrentGoalSelector.Select(stateQueries, currentGoal?.Id).Value];

        return CliNextGoalQueryRoute.TryExecute(args, args.Count == 2 ? args[1] : args[2],
            stateQueries, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock);
    }
}
