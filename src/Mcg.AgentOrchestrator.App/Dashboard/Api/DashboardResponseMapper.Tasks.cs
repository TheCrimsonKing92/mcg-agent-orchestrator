using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static TaskQueryDto ToTaskQueryDto(Goal goal, TaskQueryResult result)
{
    return new TaskQueryDto(
        result.GoalId.Value,
        result.Objective,
        result.Status,
        result.Tasks.Select(task => ToTaskSummaryDto(goal, task)).ToList());
}

public static TaskSummaryDto ToTaskSummaryDto(Goal goal, TaskSpec task)
{
    return new TaskSummaryDto(
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.RequiredRole,
        task.Status,
        task.Description,
        ConsoleViews.FormatTaskEvidence(task),
        task.VerificationPlan,
        task.AssignedAgentId?.Value,
        task.SubscriptionRetryAfter);
}

public static TaskDetailDto ToTaskDetailDto(Goal goal, TaskSpec task)
{
    return new TaskDetailDto(
        ToTaskSummaryDto(goal, task),
        task.LastExecution is null
            ? null
            : new ExecutionDto(
                task.LastExecution.AgentId.Value,
                task.LastExecution.AgentName,
                task.LastExecution.ProviderName,
                task.LastExecution.ModelName,
                task.LastExecution.Output,
                task.LastExecution.StopReason,
                task.LastExecution.Usage?.InputTokens,
                task.LastExecution.Usage?.OutputTokens,
                task.LastExecution.CompletedAt),
        task.LastDispatch is null
            ? null
            : new DispatchDto(task.LastDispatch.WorkerName, task.LastDispatch.Command, task.LastDispatch.WorkingDirectory, task.LastDispatch.DispatchedAt),
        ToProcessDto(task.LastProcess),
        task.LastVerification is null
            ? null
            : new VerificationDto(
                task.LastVerification.Command,
                task.LastVerification.ExitCode,
                task.LastVerification.Succeeded,
                task.LastVerification.CompletedAt,
                task.VerificationHistory.Count),
        goal.Timeline
            .Where(evt => evt.TaskId == task.Id)
            .Select(evt => new TimelineDto(evt.Kind, evt.Message, evt.OccurredAt))
            .ToList());
}

public static ProcessDto? ToProcessDto(TaskProcessRecord? process)
{
    return process is null
        ? null
        : new ProcessDto(
            process.ProcessId,
            process.Command,
            process.IsRunning,
            process.StartedAt,
            process.CompletedAt,
            process.ExitCode,
            process.WasCancelled,
            process.StandardOutputPath,
            process.StandardErrorPath,
            process.ExitCodePath);
}

public static ProcessLogDto ToProcessLogDto(Goal goal, TaskSpec task)
{
    var process = task.LastProcess
        ?? throw new InvalidOperationException("Task has no background process logs.");
    var logs = ProcessLogReader.Read(process);
    return new ProcessLogDto(
        goal.Id.Value,
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        process.ProcessId,
        process.IsRunning,
        process.ExitCode,
        logs.StandardOutputPath,
        logs.StandardOutput,
        logs.StandardErrorPath,
        logs.StandardError,
        logs.ExitCodePath,
        logs.ExitCode);
}

public static TaskVerificationHistoryDto ToVerificationHistoryDto(Goal goal, TaskSpec task)
{
    return new TaskVerificationHistoryDto(
        goal.Id.Value,
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.VerificationHistory.Select((verification, index) => new VerificationHistoryEntryDto(
            index + 1,
            verification.Command,
            verification.WorkingDirectory,
            verification.ExitCode,
            verification.Succeeded,
            verification.StandardOutput,
            verification.StandardError,
            verification.CompletedAt)).ToList());
}

public static TaskVerificationPlanDto ToTaskVerificationPlanDto(Goal goal, TaskSpec task)
{
    return new TaskVerificationPlanDto(
        goal.Id.Value,
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.VerificationPlan);
}

public static TaskTimelineDto ToTaskTimelineDto(Goal goal, TaskSpec task)
{
    return new TaskTimelineDto(
        goal.Id.Value,
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        goal.Timeline
            .Where(evt => evt.TaskId == task.Id)
            .OrderBy(evt => evt.OccurredAt)
            .Select(evt => new TimelineDto(evt.Kind, evt.Message, evt.OccurredAt))
            .ToList());
}
}
