using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintProcessLogs(TaskSpec task, ProcessLogStream stream)
{
    var process = task.LastProcess
        ?? throw new InvalidOperationException("Task has no background process logs.");
    var logs = ProcessLogReader.Read(process);

    foreach (var line in ProcessHeartbeatText.FormatLines(logs.Heartbeat))
    {
        Console.WriteLine(line);
    }

    if (stream is ProcessLogStream.All or ProcessLogStream.Stdout)
    {
        Console.WriteLine($"stdout: {logs.StandardOutputPath}");
        Console.WriteLine(string.IsNullOrEmpty(logs.StandardOutput) ? "<empty>" : logs.StandardOutput.TrimEnd());
    }

    if (stream is ProcessLogStream.All or ProcessLogStream.Stderr)
    {
        Console.WriteLine($"stderr: {logs.StandardErrorPath}");
        Console.WriteLine(string.IsNullOrEmpty(logs.StandardError) ? "<empty>" : logs.StandardError.TrimEnd());
    }

    if (stream is ProcessLogStream.All or ProcessLogStream.Exit)
    {
        Console.WriteLine($"exit: {logs.ExitCodePath}");
        Console.WriteLine(string.IsNullOrEmpty(logs.ExitCode) ? "<missing>" : logs.ExitCode.Trim());
    }
}
}


