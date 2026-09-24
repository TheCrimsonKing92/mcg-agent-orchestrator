using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static void ValidateGoalRefinementArguments(IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 3 || string.IsNullOrWhiteSpace(args[1]))
            throw new InvalidOperationException($"Usage: {GoalRefinementWorkCoordinator.CommandName} <goal-id> [executor-stamp]");
        if (args[1].Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                $"Usage: {GoalRefinementWorkCoordinator.CommandName} <goal-id> [executor-stamp]. " +
                $"The <goal-id> argument '{args[1]}' contains whitespace.");
    }
}
