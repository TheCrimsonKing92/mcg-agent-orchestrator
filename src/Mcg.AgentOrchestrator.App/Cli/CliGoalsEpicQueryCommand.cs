using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalsEpicQueryCommand
{
    public static bool IsGoalsEpicQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count is not (2 or 3) ||
            !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        string reference;
        if (args.Count == 3 && args[1].Equals("--epic", StringComparison.OrdinalIgnoreCase))
            reference = args[2];
        else if (args.Count == 2 && args[1].StartsWith("--epic=", StringComparison.OrdinalIgnoreCase))
            reference = args[1]["--epic=".Length..];
        else
            return false;

        return !string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("--", StringComparison.Ordinal);
    }

    public static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        ConsoleViews.PrintGoals(EpicProgressReadModel.ListMemberGoals(
            workspace, args.Count == 3 ? args[2] : args[1]["--epic=".Length..]));
}
