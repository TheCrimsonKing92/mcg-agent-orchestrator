namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
public static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context)
{
    var command = parts[0].ToLowerInvariant();
    var handled =
        TryExecuteSystemCommand(command, parts, context) ??
        TryExecuteGoalCommand(command, parts, context) ??
        TryExecuteTaskCommand(command, parts, context) ??
        TryExecuteWorkerCommand(command, parts, context);

    if (handled is not null)
    {
        return handled.Value;
    }

    Console.WriteLine("Unknown command.");
    return false;
}
}
