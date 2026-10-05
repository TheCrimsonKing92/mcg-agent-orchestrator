namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public void RecordFailedRoundCheckpointReceipt(
        GoalId goalId, TaskId taskId, FailedRoundCheckpointReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var task = GetTask(goalId, taskId);
        if (task.RequiredRole != AgentRole.Developer || task.LastDispatch is null ||
            task.LastProcess?.WasCancelled == true)
            throw new InvalidOperationException("Failed-round receipts require an uncancelled Developer dispatch.");
        task.SetDispatchFailedRoundCheckpointReceipt(receipt);
    }

    public void RecordFailedRoundCheckpointDecision(
        GoalId goalId, TaskId taskId, DateTimeOffset dispatchedAt, FailedRoundCheckpointDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        GetTask(goalId, taskId).RecordDispatchFailedRoundCheckpointDecision(dispatchedAt, decision);
    }
}
