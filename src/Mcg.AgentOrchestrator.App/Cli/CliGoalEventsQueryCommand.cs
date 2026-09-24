using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalEventsQueryCommand
{
    internal static bool IsGoalEventsQueryCommand(IReadOnlyList<string> args) =>
        args.Count == 2 &&
        args[0].Equals("goal-events", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(args[1]) &&
        !args[1].StartsWith("-", StringComparison.Ordinal) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace)
    {
        var knownGoalIds = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Select(goal => goal.Id);
        GoalEventsCommand.RunAsync(workspace.GoalLifecycleEventsDirectory, args[1],
            follow: false, Console.Out, knownGoalIds).GetAwaiter().GetResult();
    }
}
