using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTaskQueryCommand
{
    internal static bool IsTaskQueryCommand(IReadOnlyList<string> args) =>
        args.Count >= 1 &&
        (args[0].Equals("task", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("tasks", StringComparison.OrdinalIgnoreCase)) &&
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
        if (!TryResolveGoalId(args, stateQueries, currentGoal, out var goalId) || goalId is null)
            return false;

        var commandKernel = stateQueries.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
        if (commandKernel.Goals.All(goal => goal.Id != goalId))
            return false;

        Goal? commandCurrentGoal = commandKernel.GetGoal(goalId);
        var changed = CliCommandDispatcher.ExecuteCommand(
            args,
            commandKernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref commandCurrentGoal,
            channel);
        if (changed)
        {
            throw new InvalidOperationException(
                "Task query attempted to mutate orchestrator state through its read-only route.");
        }

        currentGoal = commandCurrentGoal;
        return true;
    }

    private static bool TryResolveGoalId(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        Goal? currentGoal,
        out GoalId? goalId)
    {
        string? goalPrefix = null;
        var hasPrefixShape = false;

        if (args[0].Equals("task", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count > 1 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count < 4)
                {
                    goalId = null;
                    return false;
                }

                goalPrefix = args[2];
                hasPrefixShape = true;
            }
            else if (args.Count > 2 &&
                !args[1].StartsWith("--", StringComparison.Ordinal) &&
                !args[2].StartsWith("--", StringComparison.Ordinal) &&
                (!int.TryParse(args[1], out _) || args[1].Length >= 8))
            {
                goalPrefix = args[1];
                hasPrefixShape = true;
            }
        }

        if (!hasPrefixShape)
        {
            goalId = currentGoal?.Id;
            return currentGoal is not null;
        }

        var matches = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(goalPrefix!, StringComparison.OrdinalIgnoreCase))
            .Select(goal => new GoalId(goal.Id))
            .ToArray();
        if (matches.Length != 1)
        {
            goalId = null;
            return false;
        }

        goalId = matches[0];
        return true;
    }
}
