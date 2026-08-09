using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintEvidenceSummary(Goal goal, GoalEvidenceSummary summary)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {summary.GoalId.Value[..8]} evidence");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(summary.Objective).Text}");
    Console.WriteLine($"Status: {summary.Status}");
    Console.WriteLine($"Tasks: {summary.TotalTasks}");
    Console.WriteLine($"Execution: {summary.TasksWithExecution}; dispatch: {summary.TasksWithDispatch}; process: {summary.TasksWithProcess} (running {summary.RunningProcesses})");
    Console.WriteLine($"Tokens: {FormatTokenUsage(summary.InputTokens, summary.OutputTokens)}; potentially paid: {FormatTokenUsage(summary.PotentiallyPaidInputTokens, summary.PotentiallyPaidOutputTokens)}");
    foreach (var usage in summary.ModelUsage)
    {
        var paid = usage.IsPotentiallyPaidProvider ? " potentially paid" : string.Empty;
        var limitHits = usage.OutputTokenLimitHitCount > 0
            ? $", cap hits {usage.OutputTokenLimitHitCount}{FormatMaxOutputTokens(usage.MaxOutputTokens)}"
            : string.Empty;
        Console.WriteLine($"  model: {usage.ProviderName}/{usage.ModelName}{paid}: {usage.ExecutionCount} run(s), {FormatTokenUsage(usage.InputTokens, usage.OutputTokens)}{limitHits}");
    }
    foreach (var dispatch in summary.DispatchModelUsage)
    {
        var paid = dispatch.IsPotentiallyPaidProvider ? " potentially paid" : string.Empty;
        var complexity = dispatch.TaskComplexity is null ? string.Empty : $" {dispatch.TaskComplexity.Value}";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" reasoning {dispatch.ReasoningEffort}";
        Console.WriteLine($"  dispatch model: {dispatch.ProviderName}/{dispatch.ModelName}{complexity}{reasoning}{paid}: {dispatch.DispatchCount} dispatch(es)");
    }
    foreach (var fit in summary.ModelFit)
    {
        Console.WriteLine($"  model fit: {FormatModelFitSummary(fit)}");
    }
    Console.WriteLine($"Verification: {summary.TasksWithVerification}; passed={summary.PassedVerifications}; failed={summary.FailedVerifications}");
    Console.WriteLine($"Pending human input: {summary.PendingHumanInputCount}");

    foreach (var item in summary.Tasks)
    {
        Console.WriteLine($"  {GetTaskDisplayNumber(goal, item.TaskId)}. [{item.LatestEvidence}] {item.Role}: {OutputTextPreview.CreateSummary(item.Description).Text}");
        Console.WriteLine($"     {OutputTextPreview.CreateTimeline(item.Message).Text}");
    }

    Console.WriteLine();
}

private static string FormatTokenUsage(int? inputTokens, int? outputTokens)
{
    return $"{inputTokens?.ToString() ?? "n/a"} in / {outputTokens?.ToString() ?? "n/a"} out";
}

private static string FormatMaxOutputTokens(int? maxOutputTokens)
{
    return maxOutputTokens is null ? string.Empty : $" of {maxOutputTokens}";
}

private static string FormatModelFitSummary(ModelFitSummary fit)
{
    var notes = fit.NoteCount == 1 ? "1 note" : $"{fit.NoteCount} notes";
    var counts = new List<string>();
    AddModelFitCount(counts, "adequate", fit.AdequateCount);
    AddModelFitCount(counts, "overkill", fit.OverkillCount);
    AddModelFitCount(counts, "underpowered", fit.UnderpoweredCount);
    AddModelFitCount(counts, "unknown", fit.UnknownCount);
    if (counts.Count == 0)
    {
        counts.Add("none");
    }

    var shapes = fit.TaskShapes is { Count: > 0 }
        ? $"; shapes {string.Join(", ", fit.TaskShapes)}"
        : string.Empty;
    return $"{fit.ProviderName}/{fit.ModelName}: {notes}; {string.Join(", ", counts)}{shapes}";
}

private static void AddModelFitCount(List<string> counts, string label, int count)
{
    if (count > 0)
    {
        counts.Add($"{label} {count}");
    }
}

public static void PrintStageReadinessReport(
    Goal goal,
    GoalStageReadinessReport report,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {report.GoalId.Value[..8]} SDLC stages: {(report.IsReadyForAcceptance ? "ready" : "not ready")}");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(report.Objective).Text}");
    Console.WriteLine($"Status: {report.Status}");
    Console.WriteLine($"Stages: {report.TotalStages}; verified={report.VerifiedStages}; open={report.OpenStages}; blocked={report.BlockedStages}");

    foreach (var stage in report.Stages)
    {
        var taskNumber = GetTaskDisplayNumber(goal, stage.TaskId);
        Console.WriteLine($"  {taskNumber}. [{stage.StageStatus}] {stage.Stage}: {OutputTextPreview.CreateSummary(stage.Description).Text}");
        Console.WriteLine($"     task={stage.TaskStatus}; assigned={stage.IsAssigned}; evidence={stage.LatestEvidence}; gate={stage.VerificationStatus}");
        Console.WriteLine($"     {OutputTextPreview.CreateTimeline(stage.Message).Text}");
        Console.WriteLine($"     action: {OutputTextPreview.CreateTimeline(stage.SuggestedAction).Text}");
        Console.WriteLine($"     command: {BuildStageSuggestedCommand(goal, stage, agents)}");
    }

    Console.WriteLine();
}

public static void PrintVerificationGate(Goal goal, GoalVerificationGate gate)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {gate.GoalId.Value[..8]} verification gate: {(gate.IsSatisfied ? "passed" : "not passed")}");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(gate.Objective).Text}");
    Console.WriteLine($"Status: {gate.Status}");

    foreach (var task in gate.Tasks)
    {
        Console.WriteLine($"  {GetTaskDisplayNumber(goal, task.TaskId)}. [{task.GateStatus}] {task.Role}: {OutputTextPreview.CreateSummary(task.Description).Text}");
        Console.WriteLine($"     task status: {task.TaskStatus}");
        Console.WriteLine($"     {OutputTextPreview.CreateTimeline(task.Message).Text}");
    }

    Console.WriteLine();
}

public static void PrintVerificationWorklist(Goal goal, GoalVerificationWorklist worklist)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {worklist.GoalId.Value[..8]} verification worklist: {worklist.OpenCount} open");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(worklist.Objective).Text}");
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
            Console.WriteLine($"  {taskNumber}. [{item.GateStatus}] {item.Role}: {OutputTextPreview.CreateSummary(item.Description).Text}");
            Console.WriteLine($"     {OutputTextPreview.CreateTimeline(item.Message).Text}");
            Console.WriteLine($"     action: {OutputTextPreview.CreateTimeline(item.SuggestedAction).Text}");
            Console.WriteLine($"     command: {BuildVerificationSuggestedCommand(taskNumber, item.GateStatus)}");
        }
    }

    Console.WriteLine();
}

public static void PrintHumanInputWorklist(Goal goal, GoalHumanInputWorklist worklist)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {worklist.GoalId.Value[..8]} human input worklist: {worklist.OpenCount} open");
    Console.WriteLine($"Objective: {OutputTextPreview.CreateSummary(worklist.Objective).Text}");
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
                : $"task {GetTaskDisplayNumber(goal, item.TaskId)} [{item.TaskStatus}] {item.Role}: {OutputTextPreview.CreateSummary(item.Description ?? string.Empty).Text}";
            Console.WriteLine($"  {item.RequestId.Value} {item.Kind} age={item.AgeSeconds}s occurrences={item.OccurrenceCount} {scope}");
            Console.WriteLine($"     flags: auto-defaultable={item.IsAutoDefaultable}; dismissible={item.IsDismissible}; answer-required={item.IsAnswerRequired}; externally-blocked={item.IsExternallyBlocked}");
            Console.WriteLine($"     question: {OutputTextPreview.CreateSummary(item.Question).Text}");
            Console.WriteLine($"     action: {OutputTextPreview.CreateTimeline(item.SuggestedAction).Text}");
            var defaultResumeCommand = HumanInputRequest.BuildDefaultResumeCommand(item.RequestId);
            var resumeCommand = string.Equals(item.ResumeCommand, defaultResumeCommand, StringComparison.Ordinal)
                ? $"answer {item.RequestId.Value} <answer>"
                : item.ResumeCommand;
            Console.WriteLine($"     command: {resumeCommand}");
        }
    }

    Console.WriteLine();
}

}


