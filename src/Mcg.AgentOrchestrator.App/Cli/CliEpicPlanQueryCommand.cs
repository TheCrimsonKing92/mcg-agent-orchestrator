using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliEpicPlanQueryCommand
{
    public static bool IsEpicPlanQueryCommand(IReadOnlyList<string> args) =>
        args.Count is 2 or 3 &&
        args[0].Equals("epic-plan", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(args[1]) &&
        !args[1].StartsWith("--", StringComparison.Ordinal) &&
        (args.Count == 2 || args[2].Equals("--json", StringComparison.Ordinal));

    public static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (!IsEpicPlanQueryCommand(args))
            throw new ArgumentException("Usage: epic-plan <id-or-title> [--json]");

        EpicPlanCommands.PrintPlanRead(workspace, args[1], json: args.Count == 3);
    }
}
