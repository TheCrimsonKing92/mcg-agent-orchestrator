using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintAdvanceResult(AdvanceResultDto result)
{
    Console.WriteLine();
    Console.WriteLine(result.Executed
        ? $"Advanced goal {result.GoalId[..8]}: {result.Message}"
        : $"Cannot advance goal {result.GoalId[..8]}: {result.Message}");

    if (result.Action is not null)
    {
        Console.WriteLine($"Next action: {result.Action.Kind} - {result.Action.Message}");
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
        NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber}",
        NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber} | subscription-dispatch {taskNumber}",
        NextActionKind.DelegatePendingTask => "delegate",
        NextActionKind.MonitorGoal => "monitor",
        _ => "monitor"
    };
}
}


