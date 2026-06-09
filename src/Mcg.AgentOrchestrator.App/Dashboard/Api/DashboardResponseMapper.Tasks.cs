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
    var description = ResponseTextPreview.CreateSummary(task.Description);
    var verificationPlan = task.VerificationPlan is null
        ? null
        : ResponseTextPreview.CreateSummary(task.VerificationPlan);

    return new TaskSummaryDto(
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.RequiredRole,
        task.Status,
        description.Text,
        description.IsTruncated,
        description.OriginalLength,
        ConsoleViews.FormatTaskEvidence(task),
        verificationPlan?.Text,
        verificationPlan?.IsTruncated ?? false,
        verificationPlan?.OriginalLength ?? 0,
        task.AssignedAgentId?.Value,
        task.SubscriptionRetryAfter);
}

public static TaskDetailDto ToTaskDetailDto(Goal goal, TaskSpec task)
{
    var executionOutput = task.LastExecution is null
        ? null
        : ResponseTextPreview.Create(task.LastExecution.Output);

    return new TaskDetailDto(
        ToTaskSummaryDto(goal, task),
        task.LastExecution is null
            ? null
            : new ExecutionDto(
                task.LastExecution.AgentId.Value,
                task.LastExecution.AgentName,
                task.LastExecution.ProviderName,
                task.LastExecution.ModelName,
                executionOutput!.Text,
                executionOutput.IsTruncated,
                executionOutput.OriginalLength,
                task.LastExecution.StopReason,
                task.LastExecution.Usage?.InputTokens,
                task.LastExecution.Usage?.OutputTokens,
                task.LastExecution.CompletedAt,
                task.LastExecution.TaskComplexity,
                task.LastExecution.MaxOutputTokens,
                HasOutputTokenLimitHit(task.LastExecution)),
        task.LastDispatch is null
            ? null
            : new DispatchDto(
                task.LastDispatch.WorkerName,
                task.LastDispatch.Command,
                task.LastDispatch.WorkingDirectory,
                task.LastDispatch.DispatchedAt,
                task.LastDispatch.ProviderName,
                task.LastDispatch.ModelName,
                task.LastDispatch.ReasoningEffort,
                task.LastDispatch.TaskComplexity),
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
            .Select(ToTimelineDto)
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
        task.VerificationHistory.Select((verification, index) =>
        {
            var stdout = ResponseTextPreview.Create(verification.StandardOutput);
            var stderr = ResponseTextPreview.Create(verification.StandardError);
            return new VerificationHistoryEntryDto(
                index + 1,
                verification.Command,
                verification.WorkingDirectory,
                verification.ExitCode,
                verification.Succeeded,
                stdout.Text,
                stdout.IsTruncated,
                stdout.OriginalLength,
                stderr.Text,
                stderr.IsTruncated,
                stderr.OriginalLength,
                verification.CompletedAt);
        }).ToList());
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
            .Select(ToTimelineDto)
            .ToList());
}

private static TimelineDto ToTimelineDto(ProgressEvent evt)
{
    var message = ResponseTextPreview.CreateTimeline(evt.Message);
    return new TimelineDto(
        evt.Kind,
        message.Text,
        message.IsTruncated,
        message.OriginalLength,
        evt.OccurredAt);
}

private sealed record ResponseTextPreview(string Text, bool IsTruncated, int OriginalLength)
{
    private const int PayloadMaxChars = 4000;
    private const int PayloadTailChars = 1200;
    private const int TimelineMaxChars = 600;
    private const int TimelineTailChars = 180;
    private const int SummaryMaxChars = 600;
    private const int SummaryTailChars = 180;

    public static ResponseTextPreview Create(string text)
    {
        return Create(text, PayloadMaxChars, PayloadTailChars);
    }

    public static ResponseTextPreview CreateTimeline(string text)
    {
        return Create(text, TimelineMaxChars, TimelineTailChars);
    }

    public static ResponseTextPreview CreateSummary(string text)
    {
        return Create(text, SummaryMaxChars, SummaryTailChars);
    }

    private static ResponseTextPreview Create(string text, int maxChars, int tailChars)
    {
        if (text.Length <= maxChars)
        {
            return new ResponseTextPreview(text, false, text.Length);
        }

        var headChars = maxChars - tailChars;
        var omittedChars = text.Length - headChars - tailChars;
        var marker = $"{Environment.NewLine}{Environment.NewLine}[truncated {omittedChars} chars]{Environment.NewLine}{Environment.NewLine}";
        return new ResponseTextPreview(
            text[..headChars] + marker + text[^tailChars..],
            true,
            text.Length);
    }
}

private static bool HasOutputTokenLimitHit(TaskExecutionRecord execution)
{
    return execution.MaxOutputTokens is > 0 &&
        execution.Usage?.OutputTokens is { } outputTokens &&
        outputTokens >= execution.MaxOutputTokens.Value;
}
}
