using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBacklogQueryCommand
{
    internal static bool IsBacklogQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        return args[0].ToLowerInvariant() is "backlog-list" or "backlog-show" or "backlog-similar";
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var goalIds = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Select(summary => new GoalId(summary.Id)).ToArray();
        var kernel = stateQueries.LoadGoalsAsync(goalIds).GetAwaiter().GetResult();
        Goal? commandGoal = null;
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref commandGoal);
    }
}
