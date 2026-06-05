using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintProcessBatchResult(Goal goal, ProcessBatchExecutionResult result)
{
    Console.WriteLine();
    Console.WriteLine($"{result.Plan.Action}: ready={result.Plan.ReadyCount} skipped={result.Plan.SkippedCount} changed={result.Tasks.Count}");

    foreach (var task in result.Tasks)
    {
        var process = task.LastProcess;
        var status = process?.IsRunning is true
            ? $"running pid={process.ProcessId}"
            : $"exit={process?.ExitCode?.ToString() ?? "n/a"}";
        Console.WriteLine($"  Task {GetTaskDisplayNumber(goal, task.Id)} {status}");
    }

    var skipped = result.Plan.Items
        .Where(item => item.Status == ProcessBatchItemStatus.Skipped)
        .ToList();
    if (skipped.Count > 0)
    {
        Console.WriteLine("Skipped:");
        foreach (var item in skipped)
        {
            Console.WriteLine($"  Task {GetTaskDisplayNumber(goal, item.TaskId)}: {item.Reason}");
        }
    }

    Console.WriteLine();
}

public static void PrintSubscriptionStartResult(Goal goal, SubscriptionStartResult result)
{
    Console.WriteLine();
    foreach (var dispatchResult in result.Dispatches)
    {
        Console.WriteLine($"Task {GetTaskDisplayNumber(goal, dispatchResult.Task.Id)} profile {dispatchResult.Task.LastDispatch?.WorkerName}: {dispatchResult.PromptPath}");
    }

    Console.WriteLine($"Subscription dispatches created: {result.Dispatches.Count}");
    PrintProcessBatchResult(goal, result.Processes);
}

public static void PrintNextActions(Goal goal, GoalNextActions actions)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {actions.GoalId.Value[..8]} {actions.Status}: {actions.Objective}");
    Console.WriteLine("Next actions:");

    for (var index = 0; index < actions.Items.Count; index++)
    {
        var item = actions.Items[index];
        int? taskNumber = null;
        if (item.TaskId is not null)
        {
            taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
        }

        Console.WriteLine($"  {index + 1}. {item.Kind}: {item.Message}");
        Console.WriteLine($"     command: {BuildSuggestedCommand(item, taskNumber)}");
    }

    Console.WriteLine();
}
}


