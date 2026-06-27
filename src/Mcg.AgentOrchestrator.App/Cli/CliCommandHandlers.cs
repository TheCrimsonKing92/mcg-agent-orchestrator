namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool IsHelpRequested(IReadOnlyList<string> parts) =>
    parts.Any(part =>
        part.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("-h", StringComparison.OrdinalIgnoreCase));

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
