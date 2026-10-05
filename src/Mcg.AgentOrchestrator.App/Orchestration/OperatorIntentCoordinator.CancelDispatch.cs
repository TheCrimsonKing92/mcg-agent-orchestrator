using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class OperatorIntentCoordinator
{
    internal Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> CancelLatestProcess { get; init; } =
        (kernel, goalId, taskId) => new BackgroundDispatchRunner().CancelLatestProcess(kernel, goalId, taskId);

    private void ApplyCancelDispatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent)
    {
        var payload = Deserialize<CancelDispatchOperatorIntentPayload>(intent);
        var process = task.LastProcess ??
            throw new InvalidOperationException($"Task '{task.Id}' has no background process to cancel.");
        var dispatch = task.LastDispatch ??
            throw new InvalidOperationException($"Task '{task.Id}' has no dispatch for cancellation.");
        var dispatchId = BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch);
        if (payload.ProcessId != process.ProcessId ||
            payload.ProcessStartedAt != process.StartedAt)
        {
            throw new InvalidOperationException(
                $"process-mismatch: cancel-dispatch intent {intent.Id} does not name task '{task.Id}' latest process.");
        }

        if (task.Status == WorkTaskStatus.Cancelled)
        {
            if (task.WasCancelledByConductor)
                kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled,
                    $"Operator cancelled dispatch {dispatchId}.");
            return;
        }

        if (task.Status != WorkTaskStatus.Running)
            throw new InvalidOperationException($"Task '{task.Id}' is {task.Status}; cancel-dispatch requires Running or Cancelled.");

        var cancelled = CancelLatestProcess(kernel, goal.Id, task.Id);
        if (cancelled.WasCancelled && !string.IsNullOrWhiteSpace(cancelled.WorkingDirectory))
        {
            var preservation = WorktreeEditPreservation.Preserve(
                cancelled.WorkingDirectory,
                $"operator-cancel-dispatch-{ShortGoalId(goal.Id.Value)}-{ShortGoalId(task.Id.Value)}-{cancelled.ProcessId}");
            kernel.RecordTaskNote(goal.Id, task.Id,
                $"CANCEL_DISPOSITION task_status=Cancelled redispatch=awaits-operator-retry preservation={preservation} next={BuildCancelDispatchRecoveryCommand(goal, task)}");
        }
    }

    internal static string BuildCancelDispatchRecoveryCommand(Goal goal, TaskSpec task)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == task.Id)
                return $"adjudicate --goal {ShortGoalId(goal.Id.Value)} {index + 1} route --cause <cause> --text-file <note> --evidence <reference>";
        }

        throw new InvalidOperationException($"Task '{task.Id}' is not in goal '{goal.Id}'.");
    }
}
