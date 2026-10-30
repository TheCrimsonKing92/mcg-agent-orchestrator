using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RunningDispatchIntentWait(
    TimeProvider timeProvider, Action<TimeSpan> delay, Action<TaskIntentAdmissionEvent>? observe = null)
{
    // No longer than ConductorBatchLoop.WatchStopPollIntervalSeconds: the running-worker
    // tick budget for applying intents. One window covers every intent seen by this call.
    internal static readonly TimeSpan MaxRunningWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    internal void Execute(TaskIntentAdmissionReference reference, string decisionKey, Action action)
    {
        var started = timeProvider.GetTimestamp();
        while (true)
        {
            var completed = TaskIntentAdmission.SerializeGoal(reference.StorePath, reference.GoalId, () =>
                TaskIntentAdmission.Serialize(reference.StorePath, reference.GoalId, reference.TaskId, () =>
                {
                    var store = SqliteOperatorIntentStore.OpenAdmissionAuthority(reference.StorePath);
                    var evaluation = store.EvaluateRunningAdmission(reference, decisionKey);
                    if (evaluation.Suppression is { } denial)
                        throw new TaskIntentDispatchSuppressedException(denial);

                    var waited = timeProvider.GetElapsedTime(started);
                    if (evaluation.Pending is not null && waited < MaxRunningWait) return false;

                    // This checkpoint defers the unresolved decision, not the owned worker's
                    // action (which may resume a suspended compiler). A later checkpoint rechecks.
                    // Persist the authorization decision before a callback can resume a child
                    // or commit consumption. Event I/O failure must not follow those effects.
                    foreach (var rejection in evaluation.Rejected)
                        RecordEvent(reference, rejection with { WaitedMilliseconds = waited.TotalMilliseconds });
                    if (evaluation.Pending is { } pending)
                        RecordEvent(reference, new("deferred", reference.GoalId, reference.TaskId, reference.Version,
                            reference.DispatchedAt, pending.IntentId, pending.IntentStatus, null, waited.TotalMilliseconds));
                    action();
                    return true;
                }));
            if (completed) return;

            // Neither the goal/task mutex nor an admission transaction is held during delay.
            var remaining = MaxRunningWait - timeProvider.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) delay(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    private void RecordEvent(TaskIntentAdmissionReference reference, TaskIntentAdmissionEvent evt)
    {
        // The existing event store supplies durable unique event IDs; no admission-schema
        // change or process-local cache is needed. The admission mutex serializes check+append
        // across the worker host and conductor for this dispatch.
        var eventStore = new SqliteRunEventStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reference.StorePath))!, "run-events.db"));
        string? eventId = null;
        if (evt.Kind == "continued")
        {
            var storePath = Path.GetFullPath(reference.StorePath);
            if (OperatingSystem.IsWindows()) storePath = storePath.ToUpperInvariant();
            var identity = JsonSerializer.Serialize(new
            {
                StorePath = storePath, evt.GoalId, evt.TaskId, evt.PreparedVersion,
                DispatchedAt = evt.DispatchedAt?.ToUniversalTime(), evt.IntentId
            });
            eventId = "task-intent-continued:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (eventStore.ReadByEventIdAsync(eventId).GetAwaiter().GetResult() is not null) return;
        }
        var line = $"TASK_INTENT_DISPATCH_{evt.Kind.ToUpperInvariant()} goal={evt.GoalId} task={evt.TaskId} intent={evt.IntentId} status={evt.IntentStatus} outcome={JsonSerializer.Serialize(evt.Outcome)} task_version={evt.PreparedVersion} waited_ms={evt.WaitedMilliseconds:0.###}";
        eventStore.AppendAsync(new("task-intent.dispatch", evt.GoalId, evt.Kind, evt.IntentStatus,
            line, JsonSerializer.Serialize(evt), EventId: eventId)).GetAwaiter().GetResult();
        Console.Error.WriteLine(line);
        observe?.Invoke(evt);
    }
}
