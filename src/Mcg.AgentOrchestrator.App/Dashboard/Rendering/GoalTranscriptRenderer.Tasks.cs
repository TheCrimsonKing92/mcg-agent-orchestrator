using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
private static void RenderTask(StringBuilder text, Goal goal, int taskNumber, TaskSpec task)
{
    text.AppendLine();
    text.AppendLine($"### Task {taskNumber}: {task.RequiredRole}");
    text.AppendLine($"Description: {OutputTextPreview.CreateSummary(task.Description).Text}");
    text.AppendLine($"Status: {Display(task.Status)}");
    text.AppendLine($"Assigned agent: {(task.AssignedAgentId is null ? "unassigned" : task.AssignedAgentId.Value)}");
    text.AppendLine($"Verification plan: {(task.VerificationPlan is null ? "none" : OutputTextPreview.CreateSummary(task.VerificationPlan).Text)}");

    if (task.LastExecution is not null)
    {
        text.AppendLine($"Last execution: {task.LastExecution.ProviderName}/{task.LastExecution.ModelName} by {task.LastExecution.AgentName}");
        text.AppendLine($"Model selection: complexity={task.LastExecution.TaskComplexity?.ToString() ?? "unknown"}");
        text.AppendLine($"Stop reason: {task.LastExecution.StopReason}");
        text.AppendLine($"Usage: input={task.LastExecution.Usage?.InputTokens?.ToString() ?? "n/a"} output={task.LastExecution.Usage?.OutputTokens?.ToString() ?? "n/a"} maxOutput={task.LastExecution.MaxOutputTokens?.ToString() ?? "n/a"}");
        if (IsOutputTokenLimitHit(task.LastExecution))
        {
            text.AppendLine("Model note: possible output token cap hit.");
        }
        text.AppendLine("Output:");
        text.AppendLine(OutputTextPreview.Create(task.LastExecution.Output).Text.Trim());
    }

    if (task.LastDispatch is not null)
    {
        text.AppendLine($"Last dispatch: worker={task.LastDispatch.WorkerName} command={task.LastDispatch.Command}");
        if (!string.IsNullOrWhiteSpace(task.LastDispatch.ProviderName) && !string.IsNullOrWhiteSpace(task.LastDispatch.ModelName))
        {
            text.AppendLine($"Dispatch model: {task.LastDispatch.ProviderName}/{task.LastDispatch.ModelName} complexity={task.LastDispatch.TaskComplexity?.ToString() ?? "unknown"} reasoning={task.LastDispatch.ReasoningEffort ?? "default"}");
        }
        text.AppendLine($"Working directory: {task.LastDispatch.WorkingDirectory}");
        text.AppendLine($"Dispatched: {task.LastDispatch.DispatchedAt:u}");
    }

    if (task.LastProcess is not null)
    {
        text.AppendLine($"Last process: pid={task.LastProcess.ProcessId} running={task.LastProcess.IsRunning}");
        text.AppendLine($"Started: {task.LastProcess.StartedAt:u}");
        text.AppendLine($"Completed: {task.LastProcess.CompletedAt?.ToString("u") ?? "n/a"}");
        text.AppendLine($"Exit code: {task.LastProcess.ExitCode?.ToString() ?? "n/a"}");
    }

    text.AppendLine("Verification history:");
    if (task.VerificationHistory.Count == 0)
    {
        text.AppendLine("- none");
    }
    else
    {
        foreach (var verification in task.VerificationHistory)
        {
            text.AppendLine($"- exit={verification.ExitCode} completed={verification.CompletedAt:u} command={verification.Command}");
            if (!string.IsNullOrWhiteSpace(verification.StandardOutput))
            {
                text.AppendLine($"  stdout: {OutputTextPreview.Create(verification.StandardOutput).Text.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(verification.StandardError))
            {
                text.AppendLine($"  stderr: {OutputTextPreview.Create(verification.StandardError).Text.Trim()}");
            }
        }
    }

    text.AppendLine("Task timeline:");
    var taskEvents = goal.Timeline.Where(evt => evt.TaskId == task.Id).OrderBy(evt => evt.OccurredAt).ToList();
    if (taskEvents.Count == 0)
    {
        text.AppendLine("- none");
    }
    else
    {
        foreach (var evt in taskEvents)
        {
            text.AppendLine($"- {evt.OccurredAt:u} {Display(evt.Kind)}: {OutputTextPreview.CreateTimeline(evt.Message).Text}");
        }
    }
}

private static bool IsOutputTokenLimitHit(TaskExecutionRecord execution)
{
    return execution.MaxOutputTokens is > 0 &&
        execution.Usage?.OutputTokens is { } outputTokens &&
        outputTokens >= execution.MaxOutputTokens.Value;
}
}
