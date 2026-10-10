using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintVerificationHistory(TaskSpec task)
{
    if (task.VerificationHistory.Count == 0)
    {
        Console.WriteLine("No verification history.");
        return;
    }

    for (var index = 0; index < task.VerificationHistory.Count; index++)
    {
        var verification = task.VerificationHistory[index];
        Console.WriteLine($"{index + 1}. exit={verification.ExitCode} completed={verification.CompletedAt:u}");
        Console.WriteLine($"   command: {verification.Command}");
        Console.WriteLine($"   working directory: {verification.WorkingDirectory}");

        if (!string.IsNullOrWhiteSpace(verification.StandardOutput))
        {
            Console.WriteLine($"   stdout: {OutputTextPreview.CreateVerificationLog(verification.StandardOutput, verification.StandardOutputPath).Text.TrimEnd()}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            Console.WriteLine($"   stderr: {OutputTextPreview.CreateVerificationLog(verification.StandardError, verification.StandardErrorPath).Text.TrimEnd()}");
        }
    }
}

public static int GetTaskDisplayNumber(Goal goal, TaskId taskId)
{
    return TaskDisplayNumber.Resolve(goal, taskId);
}
}


