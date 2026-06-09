using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintEvidenceSummary(Goal goal, GoalEvidenceSummary summary)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {summary.GoalId.Value[..8]} evidence");
    Console.WriteLine($"Objective: {summary.Objective}");
    Console.WriteLine($"Status: {summary.Status}");
    Console.WriteLine($"Tasks: {summary.TotalTasks}");
    Console.WriteLine($"Execution: {summary.TasksWithExecution}; dispatch: {summary.TasksWithDispatch}; process: {summary.TasksWithProcess} (running {summary.RunningProcesses})");
    Console.WriteLine($"Tokens: {FormatTokenUsage(summary.InputTokens, summary.OutputTokens)}; potentially paid: {FormatTokenUsage(summary.PotentiallyPaidInputTokens, summary.PotentiallyPaidOutputTokens)}");
    foreach (var usage in summary.ModelUsage)
    {
        var paid = usage.IsPotentiallyPaidProvider ? " potentially paid" : string.Empty;
        Console.WriteLine($"  model: {usage.ProviderName}/{usage.ModelName}{paid}: {usage.ExecutionCount} run(s), {FormatTokenUsage(usage.InputTokens, usage.OutputTokens)}");
    }
    Console.WriteLine($"Verification: {summary.TasksWithVerification}; passed={summary.PassedVerifications}; failed={summary.FailedVerifications}");
    Console.WriteLine($"Pending human input: {summary.PendingHumanInputCount}");

    foreach (var item in summary.Tasks)
    {
        Console.WriteLine($"  {GetTaskDisplayNumber(goal, item.TaskId)}. [{item.LatestEvidence}] {item.Role}: {item.Description}");
        Console.WriteLine($"     {item.Message}");
    }

    Console.WriteLine();
}

private static string FormatTokenUsage(int? inputTokens, int? outputTokens)
{
    return $"{inputTokens?.ToString() ?? "n/a"} in / {outputTokens?.ToString() ?? "n/a"} out";
}

public static void PrintStageReadinessReport(Goal goal, GoalStageReadinessReport report)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {report.GoalId.Value[..8]} SDLC stages: {(report.IsReadyForAcceptance ? "ready" : "not ready")}");
    Console.WriteLine($"Objective: {report.Objective}");
    Console.WriteLine($"Status: {report.Status}");
    Console.WriteLine($"Stages: {report.TotalStages}; verified={report.VerifiedStages}; open={report.OpenStages}; blocked={report.BlockedStages}");

    foreach (var stage in report.Stages)
    {
        var taskNumber = GetTaskDisplayNumber(goal, stage.TaskId);
        Console.WriteLine($"  {taskNumber}. [{stage.StageStatus}] {stage.Stage}: {stage.Description}");
        Console.WriteLine($"     task={stage.TaskStatus}; assigned={stage.IsAssigned}; evidence={stage.LatestEvidence}; gate={stage.VerificationStatus}");
        Console.WriteLine($"     {stage.Message}");
        Console.WriteLine($"     action: {stage.SuggestedAction}");
        Console.WriteLine($"     command: {BuildStageSuggestedCommand(taskNumber, stage)}");
    }

    Console.WriteLine();
}

public static void PrintVerificationGate(Goal goal, GoalVerificationGate gate)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {gate.GoalId.Value[..8]} verification gate: {(gate.IsSatisfied ? "passed" : "not passed")}");
    Console.WriteLine($"Objective: {gate.Objective}");
    Console.WriteLine($"Status: {gate.Status}");

    foreach (var task in gate.Tasks)
    {
        Console.WriteLine($"  {GetTaskDisplayNumber(goal, task.TaskId)}. [{task.GateStatus}] {task.Role}: {task.Description}");
        Console.WriteLine($"     task status: {task.TaskStatus}");
        Console.WriteLine($"     {task.Message}");
    }

    Console.WriteLine();
}

public static void PrintVerificationWorklist(Goal goal, GoalVerificationWorklist worklist)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {worklist.GoalId.Value[..8]} verification worklist: {worklist.OpenCount} open");
    Console.WriteLine($"Objective: {worklist.Objective}");
    Console.WriteLine($"Status: {worklist.Status}");

    if (worklist.Items.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var item in worklist.Items)
        {
            var taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
            Console.WriteLine($"  {taskNumber}. [{item.GateStatus}] {item.Role}: {item.Description}");
            Console.WriteLine($"     {item.Message}");
            Console.WriteLine($"     action: {item.SuggestedAction}");
            Console.WriteLine($"     command: {BuildVerificationSuggestedCommand(taskNumber, item.GateStatus)}");
        }
    }

    Console.WriteLine();
}

public static void PrintHumanInputWorklist(Goal goal, GoalHumanInputWorklist worklist)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {worklist.GoalId.Value[..8]} human input worklist: {worklist.OpenCount} open");
    Console.WriteLine($"Objective: {worklist.Objective}");
    Console.WriteLine($"Status: {worklist.Status}");

    if (worklist.Items.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var item in worklist.Items)
        {
            var scope = item.TaskId is null
                ? "goal"
                : $"task {GetTaskDisplayNumber(goal, item.TaskId)} [{item.TaskStatus}] {item.Role}: {item.Description}";
            Console.WriteLine($"  {item.RequestId.Value[..8]} {scope}");
            Console.WriteLine($"     question: {item.Question}");
            Console.WriteLine($"     action: {item.SuggestedAction}");
            Console.WriteLine($"     command: {BuildHumanInputSuggestedCommand(item.RequestId)}");
        }
    }

    Console.WriteLine();
}

}


