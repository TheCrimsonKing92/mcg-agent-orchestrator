using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintAdvanceResult(
    Goal goal,
    GoalAdvanceOutcome result,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    Console.WriteLine();
    Console.WriteLine(result.Executed
        ? $"Advanced goal {result.GoalId.Value[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}"
        : $"Cannot advance goal {result.GoalId.Value[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}");

    if (result.Action is not null)
    {
        Console.WriteLine($"Next action: {result.Action.Kind} - {OutputTextPreview.CreateTimeline(result.Action.Message).Text}");
        // The suggested command is a follow-up hint, so it is deliberately built from the live goal
        // rather than from a pre-execution capture: after an executed step the operator wants the
        // command for the state they are now in. The dispatch state on the advance outcome is a
        // record of the action that ran, so that one is captured before execution instead - see
        // GoalAdvanceOutcome.ActionDispatchState.
        Console.WriteLine($"Command: {NextActionCommandAdvice.BuildSuggestedCommand(goal, result.Action, agents)}");
    }

    Console.WriteLine();
}

public static void PrintRunGoalResult(
    Goal goal,
    RunGoalService.RunGoalResult result,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    Console.WriteLine();
    var goalPrefix = goal.Id.Value[..8];
    if (result.Executed)
    {
        Console.WriteLine($"Goal {goalPrefix}: {result.CompletedTasks.Count} task(s) transitioned. {OutputTextPreview.CreateTimeline(result.StopReason).Text}");
    }
    else
    {
        Console.WriteLine($"Goal {goalPrefix}: no steps executed. {OutputTextPreview.CreateTimeline(result.StopReason).Text}");
    }

    foreach (var task in result.CompletedTasks)
    {
        var status = task.Succeeded ? "succeeded" : "failed";
        Console.WriteLine($"  Task {task.TaskNumber} {task.TaskId[..8]}: {status} - {OutputTextPreview.CreateTimeline(task.Description).Text}");
        if (!string.IsNullOrWhiteSpace(task.OutputTail))
        {
            Console.WriteLine($"    tail: {OutputTextPreview.CreateTimeline(task.OutputTail).Text}");
        }
    }

    if (result.BlockingAction is not null)
    {
        Console.WriteLine($"Blocked: {result.BlockingAction.Kind} - {OutputTextPreview.CreateTimeline(result.BlockingAction.Message).Text}");
        Console.WriteLine($"Command: {NextActionCommandAdvice.BuildSuggestedCommand(goal, result.BlockingAction, agents)}");
    }

    if (result.StopEvidence is not null)
    {
        var task = result.StopEvidence.TaskNumber is null
            ? "goal"
            : $"task {result.StopEvidence.TaskNumber} {result.StopEvidence.TaskId![..8]}";
        Console.WriteLine($"Stop evidence: {task} - {OutputTextPreview.CreateTimeline(result.StopEvidence.Reason).Text}");
        if (!string.IsNullOrWhiteSpace(result.StopEvidence.OutputTail))
        {
            Console.WriteLine($"Output tail: {OutputTextPreview.CreateVerificationLog(result.StopEvidence.OutputTail).Text}");
        }
    }

    if (result.ContinueAfter.HasValue)
    {
        Console.WriteLine($"Retry after: {result.ContinueAfter:u}");
    }

    Console.WriteLine();
}
}


