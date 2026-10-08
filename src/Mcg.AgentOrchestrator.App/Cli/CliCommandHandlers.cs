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
        TryExecuteOwnerDigestCommand(command, parts, context) ??
        TryExecuteFundamentalsAlias(command, parts, context) ??
        TryExecuteFlakeCensusCommand(command, parts, context) ??
        TryExecuteSystemCommand(command, parts, context) ??
        TryExecuteGoalCommand(command, parts, context) ??
        TryExecuteTaskCommand(command, parts, context) ??
        TryExecuteWorkerCommand(command, parts, context) ??
        TryExecutePortfolioCommand(command, parts, context) ??
        TryExecuteExperimentCommand(command, parts, context) ??
        TryExecuteBacklogCommand(command, parts, context);

    if (handled is not null)
    {
        return handled.Value;
    }

    throw new ArgumentException(CliArgumentParser.FormatUnknownCommandMessage(parts));
}

private static bool? TryExecuteOwnerDigestCommand(string command, IReadOnlyList<string> parts,
    CliExecutionContext context)
{
    if (command != "owner-digest")
        return null;
    if (CliOwnerDigestCommand.Run(parts, context.Workspace) != 0)
        throw new InvalidOperationException("Owner digest failed.");
    return false;
}
}
