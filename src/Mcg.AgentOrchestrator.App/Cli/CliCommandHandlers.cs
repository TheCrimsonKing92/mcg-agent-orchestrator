namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool IsHelpRequested(IReadOnlyList<string> parts) =>
        parts.Any(part =>
            part.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("-h", StringComparison.OrdinalIgnoreCase));

    internal static bool TryPrintStartupHelp(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 || !IsHelpRequested(parts))
        {
            return false;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "conduct":
                PrintConductUsage();
                return true;

            case "workspace":
                PrintWorkspaceUsage();
                return true;

            default:
                return false;
        }
    }

public static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context)
{
    if (CliCommandHelp.TryPrintStartupHelp(parts))
    {
        return false;
    }
    CliCommandHelp.ThrowIfInvalidFlags(parts);

    var command = parts[0].ToLowerInvariant();
    var handled =
        TryExecuteFundamentalsAlias(command, parts, context) ??
        TryExecuteSystemCommand(command, parts, context) ??
        TryExecuteGoalCommand(command, parts, context) ??
        TryExecuteTaskCommand(command, parts, context) ??
        TryExecuteWorkerCommand(command, parts, context) ??
        TryExecuteBacklogCommand(command, parts, context);

    if (handled is not null)
    {
        return handled.Value;
    }

    Console.WriteLine("Unknown command.");
    return false;
}
}
