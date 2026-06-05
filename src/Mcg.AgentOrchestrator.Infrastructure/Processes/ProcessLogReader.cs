using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ProcessLogStream
{
    All,
    Stdout,
    Stderr,
    Exit
}

public sealed record ProcessLogSnapshot(
    string StandardOutputPath,
    string StandardOutput,
    string StandardErrorPath,
    string StandardError,
    string ExitCodePath,
    string ExitCode);

public static class ProcessLogReader
{
    public static ProcessLogSnapshot Read(TaskProcessRecord process)
    {
        return new ProcessLogSnapshot(
            process.StandardOutputPath,
            ReadIfExists(process.StandardOutputPath),
            process.StandardErrorPath,
            ReadIfExists(process.StandardErrorPath),
            process.ExitCodePath,
            ReadIfExists(process.ExitCodePath));
    }

    private static string ReadIfExists(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
