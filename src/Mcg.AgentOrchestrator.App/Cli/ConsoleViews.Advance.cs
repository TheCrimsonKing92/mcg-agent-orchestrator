using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintAdvanceResult(AdvanceResultDto result)
{
    Console.WriteLine();
    Console.WriteLine(result.Executed
        ? $"Advanced goal {result.GoalId[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}"
        : $"Cannot advance goal {result.GoalId[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}");

    if (result.Action is not null)
    {
        Console.WriteLine($"Next action: {result.Action.Kind} - {OutputTextPreview.CreateTimeline(result.Action.Message).Text}");
        Console.WriteLine($"Command: {result.Action.SuggestedCommand}");
    }

    Console.WriteLine();
}

public static string BuildSuggestedCommand(NextActionItem item, int? taskNumber)
{
    return item.Kind switch
    {
        NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null
            ? "pending"
            : $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
        NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
        NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber} --confirm-dispatch-start",
        NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber}",
        NextActionKind.DelegatePendingTask => "delegate",
        NextActionKind.MonitorGoal => "monitor",
        _ => "monitor"
    };
}

public static string BuildSuggestedCommand(Goal goal, NextActionItem item)
{
    int? taskNumber = null;
    if (item.TaskId is not null)
    {
        taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
    }

    var command = BuildSuggestedCommand(item, taskNumber);
    if (item.Kind != NextActionKind.ExecuteRecordedDispatch || item.TaskId is null)
    {
        return command;
    }

    var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == item.TaskId);
    return task is not null && SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task) is not null
        ? $"{command} {SubscriptionPromptCostGuard.CliConfirmationFlag}"
        : command;
}
}


