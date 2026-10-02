namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private const int MaxCmdCommandLineLength = 8191;

    internal static void ThrowIfCmdCommandLineTooLong(string composedArguments, string[] arguments)
    {
        if (composedArguments.Length > MaxCmdCommandLineLength)
            throw new CommandLineTooLongException(composedArguments.Length, MaxCmdCommandLineLength,
                arguments.Length == 0 ? "(none)" : arguments[0]);
    }
}
