using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintDelegationPlan(Goal goal, DelegationPlan plan)
{
    if (plan.Assignments.Count == 0)
    {
        Console.WriteLine("No pending tasks were delegated.");
        return;
    }

    Console.WriteLine("Delegated tasks:");
    foreach (var assignment in plan.Assignments)
    {
        Console.WriteLine($"  task {GetTaskDisplayNumber(goal, assignment.TaskId)} -> {assignment.AgentId.Value} ({assignment.Role})");
    }
}

public static void PrintTask(Goal goal, TaskSpec task)
{
    Console.WriteLine($"{task.Id.Value[..8]} [{task.Status}] {task.RequiredRole}: {task.Description}");
    Console.WriteLine($"Assigned agent: {(task.AssignedAgentId is null ? "unassigned" : task.AssignedAgentId.Value)}");
    Console.WriteLine($"Verification plan: {task.VerificationPlan ?? "none"}");

    if (task.LastExecution is not null)
    {
        Console.WriteLine($"Last execution: {task.LastExecution.ProviderName}/{task.LastExecution.ModelName} by {task.LastExecution.AgentName}");
        Console.WriteLine($"Stop reason: {task.LastExecution.StopReason}");
        Console.WriteLine($"Usage: input={task.LastExecution.Usage?.InputTokens?.ToString() ?? "n/a"} output={task.LastExecution.Usage?.OutputTokens?.ToString() ?? "n/a"} maxOutput={task.LastExecution.MaxOutputTokens?.ToString() ?? "n/a"}");
        if (IsOutputTokenLimitHit(task.LastExecution))
        {
            Console.WriteLine("Model note: possible output token cap hit.");
        }
        Console.WriteLine("Output:");
        Console.WriteLine(task.LastExecution.Output);
    }

    if (task.LastDispatch is not null)
    {
        Console.WriteLine($"Last dispatch: worker={task.LastDispatch.WorkerName} command={task.LastDispatch.Command}");
        if (!string.IsNullOrWhiteSpace(task.LastDispatch.ProviderName) && !string.IsNullOrWhiteSpace(task.LastDispatch.ModelName))
        {
            Console.WriteLine($"Dispatch model: {task.LastDispatch.ProviderName}/{task.LastDispatch.ModelName} complexity={task.LastDispatch.TaskComplexity?.ToString() ?? "unknown"} reasoning={task.LastDispatch.ReasoningEffort ?? "default"}");
        }
        Console.WriteLine($"Working directory: {task.LastDispatch.WorkingDirectory}");
        Console.WriteLine($"Dispatched: {task.LastDispatch.DispatchedAt:u}");
    }

    if (task.LastProcess is not null)
    {
        Console.WriteLine($"Last process: pid={task.LastProcess.ProcessId} running={task.LastProcess.IsRunning}");
        Console.WriteLine($"Started: {task.LastProcess.StartedAt:u}");
        if (task.LastProcess.WasCancelled)
        {
            Console.WriteLine("Cancelled: yes");
        }
        if (task.LastProcess.CompletedAt is not null)
        {
            Console.WriteLine($"Completed: {task.LastProcess.CompletedAt:u} exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}");
        }
        Console.WriteLine($"stdout path: {task.LastProcess.StandardOutputPath}");
        Console.WriteLine($"stderr path: {task.LastProcess.StandardErrorPath}");
    }

    if (task.LastVerification is not null)
    {
        Console.WriteLine($"Last verification: exit={task.LastVerification.ExitCode} command={task.LastVerification.Command}");
        Console.WriteLine($"Completed: {task.LastVerification.CompletedAt:u}");
        Console.WriteLine($"Verification history: {task.VerificationHistory.Count}");

        if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardOutput))
        {
            Console.WriteLine("stdout:");
            Console.WriteLine(task.LastVerification.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardError))
        {
            Console.WriteLine("stderr:");
            Console.WriteLine(task.LastVerification.StandardError.TrimEnd());
        }
    }

    Console.WriteLine("Task timeline:");
    PrintTaskTimeline(goal, task);
}

public static void PrintTimeline(Goal goal)
{
    Console.WriteLine("Timeline:");
    foreach (var item in goal.Timeline.OrderBy(evt => evt.OccurredAt))
    {
        var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind} {task}: {item.Message}");
    }
}

public static void PrintTaskTimeline(Goal goal, TaskSpec task)
{
    var events = goal.Timeline.Where(evt => evt.TaskId == task.Id).OrderBy(evt => evt.OccurredAt).ToList();
    if (events.Count == 0)
    {
        Console.WriteLine("  no task timeline events");
        return;
    }

    foreach (var item in events)
    {
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind}: {item.Message}");
    }
}

public static void PrintPendingHumanInput(AgentOrchestratorKernel kernel)
{
    var pending = kernel.HumanInputRequests.Where(request => !request.IsCompleted).OrderBy(request => request.RequestedAt).ToList();
    if (pending.Count == 0)
    {
        Console.WriteLine("No pending human input.");
        return;
    }

    foreach (var request in pending)
    {
        var task = request.TaskId is null ? "goal" : request.TaskId.Value[..8];
        Console.WriteLine($"{request.Id.Value[..8]} goal={request.GoalId.Value[..8]} task={task}: {request.Question}");
    }
}

private static bool IsOutputTokenLimitHit(TaskExecutionRecord execution)
{
    return execution.MaxOutputTokens is > 0 &&
        execution.Usage?.OutputTokens is { } outputTokens &&
        outputTokens >= execution.MaxOutputTokens.Value;
}

}


