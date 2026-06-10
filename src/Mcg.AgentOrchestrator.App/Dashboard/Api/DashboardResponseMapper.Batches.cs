using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static BatchActionResultDto ToBatchActionResultDto(
    Goal goal,
    string action,
    IReadOnlyList<TaskSpec> tasks,
    IReadOnlyList<WorkerProfileDispatchResult>? dispatches = null)
{
    return new BatchActionResultDto(
        goal.Id.Value,
        action,
        tasks.Count,
        tasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        dispatches?.Select(result => ToProfileDispatchDto(goal, result)).ToList());
}

public static BatchActionResultDto ToProcessBatchActionResultDto(Goal goal, string action, ProcessBatchExecutionResult result)
{
    return new BatchActionResultDto(
        goal.Id.Value,
        action,
        result.Tasks.Count,
        result.Tasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        null,
        ToProcessBatchPlanDto(goal, result.Plan),
        result.Tasks.Select(task => new ProcessBatchOutcomeDto(
            ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task.Id.Value,
            ToProcessDto(task.LastProcess))).ToList());
}

public static BatchActionResultDto ToSubscriptionStartActionResultDto(Goal goal, SubscriptionStartResult result)
{
    var changedTasks = result.Dispatches
        .Select(dispatch => dispatch.Task)
        .Concat(result.Processes.Tasks)
        .DistinctBy(task => task.Id)
        .ToList();

    return new BatchActionResultDto(
        goal.Id.Value,
        "start-subscription-ready",
        changedTasks.Count,
        changedTasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        result.Dispatches.Select(dispatch => ToProfileDispatchDto(goal, dispatch)).ToList(),
        ToProcessBatchPlanDto(goal, result.Processes.Plan),
        result.Processes.Tasks.Select(task => new ProcessBatchOutcomeDto(
            ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task.Id.Value,
            ToProcessDto(task.LastProcess))).ToList());
}

public static ProcessBatchPlanDto ToProcessBatchPlanDto(Goal goal, ProcessBatchPlan plan)
{
    return new ProcessBatchPlanDto(
        plan.Action,
        plan.ReadyCount,
        plan.SkippedCount,
        plan.Items.Select(item =>
        {
            var description = OutputTextPreview.CreateSummary(item.Description);
            return new ProcessBatchPlanItemDto(
                ConsoleViews.GetTaskDisplayNumber(goal, item.TaskId),
                item.TaskId.Value,
                item.Role,
                description.Text,
                description.IsTruncated,
                description.OriginalLength,
                item.TaskStatus,
                item.Status,
                item.Reason);
        }).ToList());
}

public static ProfileDispatchDto ToProfileDispatchDto(Goal goal, WorkerProfileDispatchResult result)
{
    return new ProfileDispatchDto(
        ToTaskSummaryDto(goal, result.Task),
        result.PromptPath,
        result.Task.LastDispatch is null
            ? null
            : new DispatchDto(
                result.Task.LastDispatch.WorkerName,
                result.Task.LastDispatch.Command,
                result.Task.LastDispatch.WorkingDirectory,
                result.Task.LastDispatch.DispatchedAt,
                result.Task.LastDispatch.ProviderName,
                result.Task.LastDispatch.ModelName,
                result.Task.LastDispatch.ReasoningEffort,
                result.Task.LastDispatch.TaskComplexity,
                result.Task.LastDispatch.PromptCharacterCount));
}
}
