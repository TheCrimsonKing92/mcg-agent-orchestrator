using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBoardSnapshotQueryCommand
{
    internal static bool IsBoardSnapshotQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        bool Flag(int index, string flag) => args[index].Equals(flag, StringComparison.OrdinalIgnoreCase);
        bool Value(int index) => !args[index].StartsWith("--", StringComparison.Ordinal);

        return args[0].ToLowerInvariant() switch
        {
            "loop-health" => args.Count == 1 ||
                (args.Count == 3 && Flag(1, "--last") && Value(2)),
            "durations" => args.Count == 1 ||
                (args.Count == 2 && Flag(1, "--by-model")) ||
                (args.Count == 3 && Flag(1, "--since") && Value(2)) ||
                (args.Count == 4 &&
                    ((Flag(1, "--by-model") && Flag(2, "--since") && Value(3)) ||
                     (Flag(1, "--since") && Value(2) && Flag(3, "--by-model")))),
            "dispatch-value" => args.Count == 1 ||
                (args.Count == 3 && Flag(1, "--since") && Value(2)),
            "operator-inbox" => args.Count == 1 ||
                (args.Count == 2 && (Value(1) || Flag(1, "--show-acknowledged"))) ||
                (args.Count == 3 && Value(1) && Flag(2, "--show-acknowledged")),
            "brief" => (args.Count == 3 && Value(1) && Value(2) &&
                    !(args[1].Length < 8 && int.TryParse(args[1], out _))) ||
                (args.Count == 4 && Flag(1, "--goal") && Value(2) && Value(3)),
            _ => false
        };
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
        var kernel = stateQueries.LoadBoardSnapshotAsync().GetAwaiter().GetResult();
        var currentGoalId = currentGoal?.Id.Value;
        var localGoal = string.IsNullOrWhiteSpace(currentGoalId) ? null : kernel.Goals.FirstOrDefault(
            goal => goal.Id.Value.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
        localGoal ??= OrchestratorEntityResolver.GetLatestGoal(kernel);
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref localGoal);
    }
}
