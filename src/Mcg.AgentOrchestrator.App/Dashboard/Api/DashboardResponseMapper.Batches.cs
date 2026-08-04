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
    IReadOnlyList<WorkerProfileDispatchResult>? dispatches = null,
    IReadOnlyList<ReadyBlockedDiagnostic>? readyBlocked = null)
{
    return new BatchActionResultDto(
        goal.Id.Value,
        action,
        tasks.Count,
        tasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        dispatches?.Select(result => ToProfileDispatchDto(goal, result)).ToList(),
        ReadyBlocked: readyBlocked?.Select(ToReadyBlockedDiagnosticDto).ToList());
}

public static BatchActionResultDto ToProcessBatchActionResultDto(Goal goal, string action, ProcessBatchExecutionResult result)
{
    var changedTasks = result.Tasks
        .Concat((result.StartFailures ?? []).Select(failure => goal.Tasks.Single(task => task.Id == failure.TaskId)))
        .DistinctBy(task => task.Id)
        .ToList();
    return new BatchActionResultDto(
        goal.Id.Value,
        action,
        changedTasks.Count,
        changedTasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        null,
        ToProcessBatchPlanDto(goal, result.Plan),
        changedTasks.Select(task => new ProcessBatchOutcomeDto(
            ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task.Id.Value,
            ToProcessDto(task.LastProcess),
            GetStartFailureReason(result.StartFailures, task.Id))).ToList());
}

public static BatchActionResultDto ToSubscriptionStartActionResultDto(Goal goal, SubscriptionStartResult result)
{
    var changedTasks = result.Dispatches
        .Select(dispatch => dispatch.Task)
        .Concat(result.Processes.Tasks)
        .Concat((result.Processes.StartFailures ?? []).Select(failure => goal.Tasks.Single(task => task.Id == failure.TaskId)))
        .DistinctBy(task => task.Id)
        .ToList();

    return new BatchActionResultDto(
        goal.Id.Value,
        "start-subscription-ready",
        changedTasks.Count,
        changedTasks.Select(task => ToTaskDetailDto(goal, task)).ToList(),
        result.Dispatches.Select(dispatch => ToProfileDispatchDto(goal, dispatch)).ToList(),
        ToProcessBatchPlanDto(goal, result.Processes.Plan),
        result.Processes.Tasks
            .Concat((result.Processes.StartFailures ?? []).Select(failure => goal.Tasks.Single(task => task.Id == failure.TaskId)))
            .DistinctBy(task => task.Id)
            .Select(task => new ProcessBatchOutcomeDto(
            ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task.Id.Value,
            ToProcessDto(task.LastProcess),
            GetStartFailureReason(result.Processes.StartFailures, task.Id))).ToList(),
        ToParallelExecutionPlanDto(result.ParallelPlan),
        result.BlockedDiagnostics.Select(ToReadyBlockedDiagnosticDto).ToList());
}

private static string? GetStartFailureReason(
    IReadOnlyList<DispatchProcessStartFailure>? failures,
    TaskId taskId) =>
    failures?.FirstOrDefault(failure => failure.TaskId == taskId)?.Reason;

private static ReadyBlockedDiagnosticDto ToReadyBlockedDiagnosticDto(ReadyBlockedDiagnostic diagnostic) =>
    new(
        diagnostic.GoalPrefix,
        diagnostic.TaskNumber,
        diagnostic.TaskId,
        diagnostic.Provider,
        diagnostic.Reason,
        diagnostic.ToLine());

public static ParallelExecutionPlanDto ToParallelExecutionPlanDto(ParallelExecutionPlan plan)
{
    return new ParallelExecutionPlanDto(
        plan.Batches.Select(batch => new ParallelExecutionBatchDto(batch.Number, batch.IntentIds)).ToList(),
        plan.Decisions.Select(decision => new ParallelExecutionDecisionDto(
            decision.IntentId,
            decision.Disposition,
            decision.BatchNumber,
            decision.Reasons)).ToList());
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
                result.Task.LastDispatch.PromptCharacterCount,
                result.Task.LastDispatch.UsesComplexModel));
}
}
