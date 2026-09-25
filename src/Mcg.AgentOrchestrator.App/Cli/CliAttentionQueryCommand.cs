using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionQueryCommand
{
    internal static bool IsAttentionQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        (args.Count == 1 ||
         (args.Count == 2 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase)) ||
         (args.Count > 1 && args[1].Equals("show", StringComparison.OrdinalIgnoreCase))) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static bool TryExecute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        if (!File.Exists(itemsDb))
            return false;

        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        if (metadata.Any(goal =>
                goal.Status.Equals(nameof(GoalStatus.Parked), StringComparison.OrdinalIgnoreCase)))
            return false;

        var ids = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
        var kernel = ids.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        if (kernel.Goals.Count != metadata.Count || kernel.Goals.Any(goal => goal.Status == GoalStatus.Parked))
            return false;

        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal);
        return true;
    }
}
