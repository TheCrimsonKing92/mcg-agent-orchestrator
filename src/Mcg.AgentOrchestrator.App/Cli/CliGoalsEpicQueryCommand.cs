using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalsEpicQueryCommand
{
    public static bool IsGoalsEpicQueryCommand(IReadOnlyList<string> args) =>
        IsGoalsEpicQueryCommand(args, out _, out _);

    internal static bool IsGoalsEpicQueryCommand(IReadOnlyList<string> args, out string reference, out bool json)
    {
        reference = string.Empty;
        json = false;
        if (args.Count is not (2 or 3 or 4) ||
            !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        json = args[^1].Equals("--json", StringComparison.OrdinalIgnoreCase);
        var humanArgCount = args.Count - (json ? 1 : 0);
        if (humanArgCount == 3 && args[1].Equals("--epic", StringComparison.OrdinalIgnoreCase))
            reference = args[2];
        else if (humanArgCount == 2 && args[1].StartsWith("--epic=", StringComparison.OrdinalIgnoreCase))
            reference = args[1]["--epic=".Length..];
        else
            return false;

        return !string.IsNullOrWhiteSpace(reference) && !reference.StartsWith("--", StringComparison.Ordinal);
    }

    public static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (!IsGoalsEpicQueryCommand(args, out var reference, out var json))
            throw new ArgumentException("Usage: goals [--epic <id-or-title>] [--json]");
        if (json)
            PrintJson(workspace, reference);
        else
            GoalListTextView.Print(EpicProgressReadModel.ListMemberGoals(workspace, reference));
    }

    internal static void PrintJson(OrchestratorWorkspace workspace, string? reference) =>
        Console.WriteLine(GoalsAgentReadView.Serialize(EpicProgressReadModel.ListMemberGoals(workspace, reference)));

    internal static void PrintGoals(OrchestratorWorkspace workspace, IReadOnlyList<string> args, string? reference)
    {
        if (args[^1].Equals("--json", StringComparison.OrdinalIgnoreCase))
            PrintJson(workspace, reference);
        else
            GoalListTextView.Print(EpicProgressReadModel.ListMemberGoals(workspace, reference));
    }
}
