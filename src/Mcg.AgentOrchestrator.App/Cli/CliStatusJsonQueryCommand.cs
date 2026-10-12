using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliStatusJsonQueryCommand
{
    internal static bool IsStatusJsonCommand(IReadOnlyList<string> args) =>
        args.Count == 3 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith("-", StringComparison.Ordinal) &&
        args[2] == "--json" && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        var (_, goal) = CliStatusQueryCommand.ResolveGoal(stateQueries, args[1]);
        Console.WriteLine(StatusAgentReadView.Render(goal,
            CliCommandHandlers.ResolveGoalFriendlyLabel(goal, workspace.BacklogStorePath),
            CliCommandHandlers.ResolveGoalStatusText(workspace, goal), time));
    }
}
