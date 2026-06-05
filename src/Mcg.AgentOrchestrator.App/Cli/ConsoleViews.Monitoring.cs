using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintMonitor(GoalMonitor monitor)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {monitor.GoalId.Value[..8]} {monitor.Status}: {monitor.Objective}");
    Console.WriteLine($"Tasks: {monitor.TotalTasks}");
    Console.WriteLine("Status counts:");

    foreach (var count in monitor.TaskStatusCounts)
    {
        Console.WriteLine($"  {count.Status}: {count.Count}");
    }

    Console.WriteLine($"Pending human input: {monitor.PendingHumanInputCount}");

    if (monitor.LastTimelineEventAt is not null)
    {
        Console.WriteLine($"Last event: {monitor.LastTimelineEventAt:u}");
    }

    Console.WriteLine("Attention:");

    if (monitor.AttentionItems.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var item in monitor.AttentionItems)
        {
            var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
            Console.WriteLine($"  {item.Kind} {task}: {item.Message}");
        }
    }

    Console.WriteLine();
}

public static void PrintAcceptanceSummary(Goal goal, GoalAcceptanceSummary summary)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {summary.GoalId.Value[..8]} acceptance: {(summary.IsAccepted ? "accepted" : "not accepted")}");
    Console.WriteLine($"Objective: {summary.Objective}");
    Console.WriteLine($"Status: {summary.Status}");
    Console.WriteLine($"Tasks passed: {summary.PassedTasks}/{summary.TotalTasks}");
    Console.WriteLine($"Open verification: {summary.OpenVerificationCount}");
    Console.WriteLine($"Pending human input: {summary.PendingHumanInputCount}");

    if (summary.Blockers.Count == 0)
    {
        Console.WriteLine("Blockers: none");
    }
    else
    {
        Console.WriteLine("Blockers:");
        foreach (var blocker in summary.Blockers)
        {
            int? taskNumber = blocker.TaskId is null ? null : GetTaskDisplayNumber(goal, blocker.TaskId);
            var scope = taskNumber is null ? "goal" : $"task {taskNumber}";
            Console.WriteLine($"  {blocker.Kind} ({scope}): {blocker.Message}");
            Console.WriteLine($"     action: {blocker.SuggestedAction}");
            Console.WriteLine($"     command: {BuildAcceptanceSuggestedCommand(blocker, taskNumber)}");
        }
    }

    Console.WriteLine();
}
}


