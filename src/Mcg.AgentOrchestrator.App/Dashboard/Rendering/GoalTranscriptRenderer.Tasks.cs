using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
private static void RenderTask(StringBuilder text, Goal goal, int taskNumber, TaskSpec task)
{
    text.AppendLine();
    text.AppendLine($"### Task {taskNumber}: {task.RequiredRole}");
    text.AppendLine($"Description: {task.Description}");
    text.AppendLine($"Status: {Display(task.Status)}");
    text.AppendLine($"Assigned agent: {(task.AssignedAgentId is null ? "unassigned" : task.AssignedAgentId.Value)}");
    text.AppendLine($"Verification plan: {task.VerificationPlan ?? "none"}");

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
        text.AppendLine(task.LastExecution.Output.Trim());
    }

    if (task.LastDispatch is not null)
    {
        text.AppendLine($"Last dispatch: worker={task.LastDispatch.WorkerName} command={task.LastDispatch.Command}");
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
                text.AppendLine($"  stdout: {verification.StandardOutput.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(verification.StandardError))
            {
                text.AppendLine($"  stderr: {verification.StandardError.Trim()}");
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
            text.AppendLine($"- {evt.OccurredAt:u} {Display(evt.Kind)}: {evt.Message}");
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
