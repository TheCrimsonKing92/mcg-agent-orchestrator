using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliStatusQueryCommand
{
    internal static bool IsStatusQueryCommand(IReadOnlyList<string> args) =>
        args.Count == 2 &&
        args[0].Equals("status", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(args[1]) &&
        !args[1].StartsWith("-", StringComparison.Ordinal) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

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
        var matches = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var goalId = matches.Length switch
        {
            1 => new GoalId(matches[0].Id),
            0 => throw new KeyNotFoundException($"Goal '{args[1]}' was not found."),
            _ => throw new InvalidOperationException($"Goal prefix '{args[1]}' is ambiguous.")
        };
        var kernel = stateQueries.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
        var goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId)
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var commandCurrentGoal = goal;
        var changed = CliCommandDispatcher.ExecuteCommand(args, kernel, workspace, ref agents,
            providers, ref workerProfiles, ref commandCurrentGoal, channel);
        if (changed)
            throw new InvalidOperationException("Status query attempted to mutate orchestrator state through its read-only route.");

        currentGoal = commandCurrentGoal;
    }
}
