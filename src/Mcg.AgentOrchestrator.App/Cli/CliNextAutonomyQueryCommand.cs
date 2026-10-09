using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliNextAutonomyQueryCommand
{
    internal static bool IsNextAutonomyQueryCommand(IReadOnlyList<string> args) =>
        CliNextBareAutonomyForm.IsForm(args) ||
        args.Count == 4 && args[0].Equals("next", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith("-", StringComparison.Ordinal) &&
        (args[2].Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
         args[2].Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(args[3]) && !CliCommandHelp.IsCommandSpecificHelp(args);

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
        if (CliNextBareAutonomyForm.IsForm(args))
        {
            stateQueries = CliNextBareAutonomyForm.ShareGoalMetadata(stateQueries);
            args = [args[0], CliCurrentGoalSelector.Select(stateQueries, currentGoal?.Id).Value, args[1], args[2]];
        }

        return CliNextGoalQueryRoute.TryExecute(args, args[1], stateQueries, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock);
    }
}
