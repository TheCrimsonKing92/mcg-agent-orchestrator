using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static TaskQueryDto ToTaskQueryDto(Goal goal, TaskQueryResult result)
{
    return new TaskQueryDto(
        result.GoalId.Value,
        OutputTextPreview.CreateSummary(result.Objective).Text,
        result.Status,
        result.Tasks.Select(task => ToTaskSummaryDto(goal, task)).ToList());
}

public static TaskSummaryDto ToTaskSummaryDto(Goal goal, TaskSpec task)
{
    var description = OutputTextPreview.CreateSummary(task.Description);
    var verificationPlan = task.VerificationPlan is null
        ? null
        : OutputTextPreview.CreateSummary(task.VerificationPlan);
    var workerResultSkills = ExtractWorkerResultSkills(task.LastVerification);

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
        task.SubscriptionRetryAfter,
        workerResultSkills,
        workerResultSkills.Length > 0);
}

public static TaskDetailDto ToTaskDetailDto(Goal goal, TaskSpec task)
{
    var executionOutput = task.LastExecution is null
        ? null
        : OutputTextPreview.Create(task.LastExecution.Output);

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
                HasOutputTokenLimitHit(task.LastExecution),
                task.LastExecution.PromptCharacterCount),
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
                task.LastDispatch.TaskComplexity,
                task.LastDispatch.PromptCharacterCount,
                task.LastDispatch.UsesComplexModel),
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
            process.ExitCodePath,
            ToDispatchHeartbeatDto(ProcessLogReader.ReadHeartbeat(process)));
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
        logs.ExitCode,
        ToDispatchHeartbeatDto(logs.Heartbeat));
}

private static string[] ExtractWorkerResultSkills(TaskVerificationRecord? verification)
{
    if (verification is null)
    {
        return [];
    }

    var lines = $"{verification.StandardOutput}\n{verification.StandardError}"
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var start = Array.FindIndex(lines, line => line.Equals("WORKER_RESULT:", StringComparison.OrdinalIgnoreCase));
    if (start < 0)
    {
        return [];
    }

    var end = Array.FindIndex(lines, start + 1, line => line.Equals("END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase));
    if (end < 0)
    {
        return [];
    }

    var skills = lines
        .Skip(start + 1)
        .Take(end - start - 1)
        .FirstOrDefault(line => line.StartsWith("skills:", StringComparison.OrdinalIgnoreCase));
    if (string.IsNullOrWhiteSpace(skills))
    {
        return [];
    }

    return skills["skills:".Length..]
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(skill => !skill.Equals("none", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

private static DispatchHeartbeatDto ToDispatchHeartbeatDto(DispatchHeartbeatStatus heartbeat)
{
    return new DispatchHeartbeatDto(
        heartbeat.Path,
        heartbeat.IsAvailable,
        heartbeat.UnavailableReason,
        heartbeat.ProcessId,
        heartbeat.ChildProcessId,
        heartbeat.OwnedProcessIds,
        heartbeat.State,
        heartbeat.LastObservedAt,
        heartbeat.LastProgressAt,
        heartbeat.HeartbeatAge?.TotalSeconds,
        heartbeat.IdleDuration?.TotalSeconds,
        heartbeat.StandardOutputBytes,
        heartbeat.StandardErrorBytes);
}

public static TaskVerificationHistoryDto ToVerificationHistoryDto(Goal goal, TaskSpec task)
{
    return new TaskVerificationHistoryDto(
        goal.Id.Value,
        ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
        task.Id.Value,
        task.VerificationHistory.Select((verification, index) =>
        {
            var stdout = OutputTextPreview.CreateVerificationLog(verification.StandardOutput, verification.StandardOutputPath);
            var stderr = OutputTextPreview.CreateVerificationLog(verification.StandardError, verification.StandardErrorPath);
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
                verification.CompletedAt,
                verification.StandardOutputPath,
                verification.StandardErrorPath);
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
    var message = OutputTextPreview.CreateTimeline(evt.Message);
    return new TimelineDto(
        evt.Kind,
        message.Text,
        message.IsTruncated,
        message.OriginalLength,
        evt.OccurredAt);
}

private static bool HasOutputTokenLimitHit(TaskExecutionRecord execution)
{
    return OutputTokenLimit.IsHit(execution);
}
}
