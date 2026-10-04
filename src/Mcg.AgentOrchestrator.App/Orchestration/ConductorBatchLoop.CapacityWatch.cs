using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly record struct RunningDispatchIdentity(string GoalId, string TaskId, int ProcessId);

    private readonly HashSet<GoalId> _capacityHeldThisTick = [];
    private DateTimeOffset? _capacityObservedAt;
    private HashSet<RunningDispatchIdentity>? _capacityWatchSet;
    private DateTimeOffset _capacityWatchSince;
    private bool _capacityWatchEmitted;

    private void ResetWorkerCapacityWatch()
    {
        _capacityHeldThisTick.Clear();
        _capacityObservedAt = null;
        _capacityWatchSet = null;
        _capacityWatchSince = default;
        _capacityWatchEmitted = false;
    }

    internal void TrackGoalOutcomeAndCapacity(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines,
        ConductEventLogWriter? conductEventLogWriter = null)
    {
        if (outcome is ConductorAdvanceOutcome.Held { Owner: ConductorHoldOwner.WorkerCapacity })
        {
            _capacityHeldThisTick.Add(goal.Id);
            if (_capacityObservedAt is null || observedAt > _capacityObservedAt.Value)
                _capacityObservedAt = observedAt;
        }

        TrackGoalOutcome(kernel, driver, goal, outcome, observedAt, stallThreshold,
            changedGoalIds, tickLines, conductEventLogWriter);
    }

    internal void ObserveWorkerCapacityTick(
        AgentOrchestratorKernel kernel,
        TimeSpan stallThreshold,
        ConductEventLogWriter? conductEventLogWriter = null)
    {
        var heldCount = _capacityHeldThisTick.Count;
        var observedAt = _capacityObservedAt;
        _capacityHeldThisTick.Clear();
        _capacityObservedAt = null;
        // Compare only capacity-holding ticks; quiet ticks preserve the previous baseline.
        if (heldCount == 0) return;

        try
        {
            if (observedAt is null)
                throw new InvalidOperationException("Capacity-held tick has no observation timestamp.");

            var running = kernel.Goals.SelectMany(goal => goal.Tasks
                .Where(task => task.LastProcess is { IsRunning: true })
                .Select(task => new RunningDispatchIdentity(
                    goal.Id.Value, task.Id.Value, task.LastProcess!.ProcessId))).ToHashSet();
            if (_capacityWatchSet is null || !_capacityWatchSet.SetEquals(running))
            {
                _capacityWatchSet = running;
                _capacityWatchSince = observedAt.Value;
                _capacityWatchEmitted = false;
            }

            var unchangedFor = observedAt.Value - _capacityWatchSince;
            if (_capacityWatchEmitted || unchangedFor < stallThreshold) return;

            // One diagnostic attempt per unchanged set, even if the event sink fails.
            _capacityWatchEmitted = true;
            var dispatches = running.OrderBy(item => ShortGoalId(item.GoalId), StringComparer.Ordinal)
                .ThenBy(item => ShortGoalId(item.TaskId), StringComparer.Ordinal).ThenBy(item => item.ProcessId)
                .ThenBy(item => item.GoalId, StringComparer.Ordinal).ThenBy(item => item.TaskId, StringComparer.Ordinal)
                .Select(item => $"goal:{ShortGoalId(item.GoalId)}/task:{ShortGoalId(item.TaskId)}/pid:{item.ProcessId.ToString(CultureInfo.InvariantCulture)}");
            var detail = FormattableString.Invariant(
                $"WORKER_CAPACITY_STALLED heldAtCapacity={heldCount} unchangedForSeconds={Math.Max(0, (long)unchangedFor.TotalSeconds)} running={running.Count} dispatches={(running.Count == 0 ? "none" : string.Join(",", dispatches))}");
            var writer = conductEventLogWriter ?? CurrentConductEventLogWriter.Value ?? _conductEventLogWriter;
            if (writer?.AppendRequired("worker-capacity-stalled", null, detail, observedAt.Value) != true)
                throw new IOException("Capacity stall conduct event could not be appended.");
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"WORKER_CAPACITY_WATCH_FAILED exception={ex.GetType().Name} " +
                    $"message={SanitizeHandoffDetail(ex.Message)}");
                Console.Error.Flush();
            }
            catch { /* Console diagnostics are best effort during fault isolation. */ }
        }
    }
}
