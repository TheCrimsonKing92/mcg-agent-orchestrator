using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    public IOperatorIntentStore? OperatorIntents { get; init; }
    public Action<string> RequeueRefusalLog { get; init; } = Console.WriteLine;

    // The CLI can stop a worker without writing the task's durable state. The tick
    // subsequently applies the intent and records the operator cancellation.
    public void StopProcessTree(TaskProcessRecord process) =>
        _ = ReapForCancellation(process, bypassTrackedJobRegistry: false);

    private TaskProcessResourceAccounting? ReapForCancellation(
        TaskProcessRecord processRecord,
        bool bypassTrackedJobRegistry)
    {
        TaskProcessResourceAccounting? resourceAccounting = null;
        if (processRecord.IsRunning)
        {
            if (bypassTrackedJobRegistry)
            {
                resourceAccounting = _recoveryService.SnapshotTrackedProcessAccounting(processRecord);
                _recoveryService.TryKillTrackedProcesses(processRecord, waitForExit: true, bypassTrackedJobRegistry: true);
                if (resourceAccounting is not null)
                    resourceAccounting = resourceAccounting with { Reaped = true };
            }
            else
            {
                try
                {
                    resourceAccounting = _recoveryService.ReapTrackedProcessJobs(processRecord, waitForExit: true);
                }
                catch (ArgumentException)
                {
                    // An already-exited process still needs its operator cancellation recorded.
                }
            }
        }

        if (!bypassTrackedJobRegistry)
            resourceAccounting ??= _recoveryService.ReleaseTrackedProcessJobs(processRecord);
        return resourceAccounting;
    }

    private bool IsRequeueRefusedByOperatorCancelIntent(GoalId goalId, TaskSpec task, string dispatchId)
    {
        if (OperatorIntents is null || task.LastProcess is not { } latest)
            return false;

        IReadOnlyList<OperatorIntentRecord> intents;
        try
        {
            intents = OperatorIntents.ListForGoalAsync(goalId.Value, int.MaxValue)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            RequeueRefusalLog($"REQUEUE_REFUSED goal={goalId.Value[..8]} task={task.Id.Value[..8]} " +
                $"dispatch={dispatchId} reason=operator-intent-store-unreadable detail={ex.Message}");
            return true;
        }

        foreach (var intent in intents)
        {
            if (intent.Verb != OperatorIntentVerbs.CancelDispatch ||
                intent.TaskId != task.Id.Value ||
                intent.Status is not (OperatorIntentStatus.Pending or OperatorIntentStatus.Claimed or OperatorIntentStatus.Applied))
                continue;

            CancelDispatchOperatorIntentPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(
                    intent.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                continue;
            }

            if (payload is null || payload.ProcessId != latest.ProcessId ||
                payload.ProcessStartedAt != latest.StartedAt || payload.DispatchId != dispatchId)
                continue;

            RequeueRefusalLog($"REQUEUE_REFUSED goal={goalId.Value[..8]} task={task.Id.Value[..8]} " +
                $"dispatch={dispatchId} intent={intent.Id} status={intent.Status} reason=operator-cancel-intent; " +
                $"Skipped auto-requeue: operator cancel-dispatch intent {intent.Id} names process {latest.ProcessId}.");
            return true;
        }

        return false;
    }
}
