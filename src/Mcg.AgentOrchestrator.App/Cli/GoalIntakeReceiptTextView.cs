using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalIntakeReceiptTextView
{
public static void PrintGoalIntakeReceipt(GoalIntakeRequestRecord record)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        kind = "goal-intake-receipt",
        requestKey = record.RequestKey,
        state = record.State,
        createdAt = record.CreatedAt,
        updatedAt = record.UpdatedAt,
        goalId = record.GoalId,
        failureCode = record.FailureCode,
        failureDetail = record.FailureDetail,
        stdoutPath = record.StdoutPath,
        stderrPath = record.StderrPath
    }));
}
}
