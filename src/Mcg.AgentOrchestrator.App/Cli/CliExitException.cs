namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class CliExitException : Exception
{
    public CliExitException(int exitCode)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
