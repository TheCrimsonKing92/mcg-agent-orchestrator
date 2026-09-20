using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DispatchStartRefusalReasonBuilder
{
    internal static string Build(
        ProcessBatchExecutionResult result,
        IReadOnlyList<WorkerProfileDispatchResult>? preparedDispatches = null)
    {
        var preparedTaskIds = preparedDispatches?
            .Select(dispatch => dispatch.Task.Id)
            .ToHashSet() ?? [];
        var refusal = result.StartRefusals?.FirstOrDefault(candidate =>
            preparedTaskIds.Count == 0 || preparedTaskIds.Contains(candidate.TaskId));
        if (refusal is not null)
            return Format(refusal.TaskId, refusal.Reason);

        var preparedSkipped = result.Plan.Items.FirstOrDefault(item =>
            item.Status == ProcessBatchItemStatus.Skipped && preparedTaskIds.Contains(item.TaskId));
        if (preparedSkipped is not null)
            return Format(preparedSkipped.TaskId, preparedSkipped.Reason);

        var fallback = result.Plan.Items
            .Where(item => item.Status == ProcessBatchItemStatus.Skipped)
            .OrderBy(item => item.TaskStatus switch
            {
                WorkTaskStatus.Running => 0,
                WorkTaskStatus.Assigned => 1,
                _ => 2
            })
            .Select(item => item.Reason)
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        return fallback is null
            ? "Dispatch recorded but no process was startable."
            : $"Dispatch recorded but no process was startable: {fallback}";
    }

    private static string Format(TaskId taskId, string reason) =>
        $"Dispatch recorded but no process was startable: task {taskId.Value[..8]}: {reason}";
}
