using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProcessLogsQueryCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => TryGetGoalPrefix(args, out _);

    private static bool TryGetGoalPrefix(IReadOnlyList<string> args, out string prefix)
    {
        prefix = string.Empty;
        if (args.Count == 0 || !args[0].Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        var count = args.Count;
        if (count >= 3 && CliCommandHandlers.IsLogStreamKeyword(args[^1]))
            count--;

        string task;
        if (count == 4 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            prefix = args[2];
            task = args[3];
        }
        else if (count == 4 && args[2].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            prefix = args[3];
            task = args[1];
        }
        else if (count == 3)
        {
            prefix = args[1];
            task = args[2];
        }
        else
            return false;

        return !string.IsNullOrWhiteSpace(prefix) &&
               !prefix.StartsWith("--", StringComparison.Ordinal) &&
               !task.StartsWith("--", StringComparison.Ordinal);
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (!TryGetGoalPrefix(args, out var prefix))
            throw new ArgumentException("Explicit-goal logs arguments are required.", nameof(args));

        var metadata = stateQueries.ListGoalIdStatusesAsync().GetAwaiter().GetResult();
        var goals = new List<GoalSnapshot>();
        var requests = new List<HumanInputRequestSnapshot>();
        foreach (var goal in metadata.Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var snapshot = stateQueries.LoadGoalsAsync([new GoalId(goal.Id)])
                .GetAwaiter().GetResult().ExportSnapshot();
            goals.AddRange(snapshot.Goals);
            requests.AddRange(snapshot.HumanInputRequests);
        }

        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(goals, requests));
        var commandGoal = currentGoal;
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref commandGoal);
        currentGoal = commandGoal;
    }
}
