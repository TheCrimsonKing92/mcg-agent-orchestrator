namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool IsHelpRequested(IReadOnlyList<string> parts) =>
        parts.Any(part =>
            part.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("-h", StringComparison.OrdinalIgnoreCase));

    internal static bool TryPrintStartupHelp(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 ||
            (!IsHelpRequested(parts) && !parts[0].Equals("help", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return CliCommandHelp.TryPrintStartupHelp(parts);
    }

public static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context)
{
    if (CliCommandHelp.TryPrintStartupHelp(parts))
    {
        return false;
    }
    CliCommandHelp.ThrowIfInvalidFlags(parts);

    var command = parts[0].ToLowerInvariant();
    if (command == "operator-commands")
    {
        CliCommandHelp.TryPrintStartupHelp(["operator-commands", "--help"]);
        return false;
    }

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
