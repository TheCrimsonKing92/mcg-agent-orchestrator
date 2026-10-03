using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static readonly object ParallelAcceptanceFairnessGate = new();
    private static string? s_parallelAcceptanceOldestWaiter;
    private static int s_parallelAcceptanceConsecutiveOvertakes;
    private static string? s_parallelAcceptanceObservedOldestWaiter;
    private static int s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks;

    internal static Goal? SelectOldestParallelAcceptanceWaiter(
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlySet<string> liveAttemptGoalIds) =>
        orderedEligible.FirstOrDefault(goal => !liveAttemptGoalIds.Contains(goal.Id.Value));

    internal static ParallelAcceptanceOldestWaiterObservation ObserveOldestParallelAcceptanceWaiter(
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlySet<string> liveAttemptGoalIds)
    {
        var waiter = SelectOldestParallelAcceptanceWaiter(orderedEligible, liveAttemptGoalIds);
        lock (ParallelAcceptanceFairnessGate)
        {
            if (waiter is null)
            {
                s_parallelAcceptanceObservedOldestWaiter = null;
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 0;
                return new ParallelAcceptanceOldestWaiterObservation(null, 0);
            }

            if (!string.Equals(
                    s_parallelAcceptanceObservedOldestWaiter,
                    waiter.Id.Value,
                    StringComparison.Ordinal))
            {
                s_parallelAcceptanceObservedOldestWaiter = waiter.Id.Value;
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 1;
            }
            else if (s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks < int.MaxValue)
            {
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks++;
            }

            return new ParallelAcceptanceOldestWaiterObservation(
                waiter,
                s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks);
        }
    }

    internal static bool ShouldDeferForParallelAcceptanceFairness(string oldestGoalId)
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            if (!string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
                return false;
            }

            return s_parallelAcceptanceConsecutiveOvertakes >= ParallelAcceptanceBoundedOvertakeLimit;
        }
    }

    internal static void ResetParallelAcceptanceFairnessForTests()
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            s_parallelAcceptanceOldestWaiter = null;
            s_parallelAcceptanceConsecutiveOvertakes = 0;
            s_parallelAcceptanceObservedOldestWaiter = null;
            s_parallelAcceptanceObservedOldestWaiterConsecutiveTicks = 0;
        }
    }

    private static void RecordParallelAcceptanceFairnessGrant(string goalId, string? oldestGoalId)
    {
        if (string.IsNullOrWhiteSpace(oldestGoalId))
        {
            return;
        }

        lock (ParallelAcceptanceFairnessGate)
        {
            if (string.Equals(goalId, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
                return;
            }

            if (!string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal))
            {
                s_parallelAcceptanceOldestWaiter = oldestGoalId;
                s_parallelAcceptanceConsecutiveOvertakes = 0;
            }

            s_parallelAcceptanceConsecutiveOvertakes++;
        }
    }

    private static void RecordParallelAcceptanceFairnessCapIfReached(
        Goal goal,
        Goal? oldestWaiter,
        int tick,
        List<string> changedGoalLines)
    {
        if (oldestWaiter is null ||
            goal.Id == oldestWaiter.Id ||
            !IsParallelAcceptanceFairnessAtLimit(oldestWaiter.Id.Value))
        {
            return;
        }

        RecordParallelAcceptanceProgress(
            $"ADMISSION tick={tick} result=cap-reached reason=parallel-acceptance-fairness goal={goal.Id.Value[..8]} oldest={oldestWaiter.Id.Value[..8]} limit={ParallelAcceptanceBoundedOvertakeLimit}",
            changedGoalLines);
    }

    private static void RecordParallelAcceptanceFairnessAdmission(
        Goal goal,
        Goal? oldestWaiter,
        ParallelAcceptanceOldestWaiterObservation? stalledOldestBypass,
        int tick,
        List<string> changedGoalLines)
    {
        if (stalledOldestBypass is { } bypass && bypass.Waiter is not null)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} result=admitted reason=parallel-acceptance-stalled-oldest-bypass goal={goal.Id.Value[..8]} bypassedOldest={bypass.Waiter.Id.Value[..8]} stalledTicks={bypass.ConsecutiveTicks} threshold={ParallelAcceptanceOldestWaiterStallTickThreshold}",
                changedGoalLines);
            return;
        }

        RecordParallelAcceptanceFairnessCapIfReached(goal, oldestWaiter, tick, changedGoalLines);
    }

    private static bool IsParallelAcceptanceFairnessAtLimit(string oldestGoalId)
    {
        lock (ParallelAcceptanceFairnessGate)
        {
            return string.Equals(s_parallelAcceptanceOldestWaiter, oldestGoalId, StringComparison.Ordinal) &&
                s_parallelAcceptanceConsecutiveOvertakes >= ParallelAcceptanceBoundedOvertakeLimit;
        }
    }

    private static void RecordParallelAcceptanceProgress(string line, List<string> changedGoalLines) =>
        changedGoalLines.Add(line);
}
