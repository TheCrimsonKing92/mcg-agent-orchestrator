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

        CancelLatestProcess(kernel, goal.Id, task.Id);
    }
}
