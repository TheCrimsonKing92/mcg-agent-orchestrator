using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAcceptanceCohortTerminalReceiptConsumption
{
    internal static IReadOnlyList<(AcceptanceCohortReceipt Receipt,
        IReadOnlyList<AcceptanceCohortPartitionReceipt> Partitions, IReadOnlySet<GoalId> RecordedGoalIds)>
        Consume(CohortAcceptanceStore? store, IReadOnlyCollection<Goal> goals,
            Func<AcceptanceCohortReceipt, Goal, AcceptanceCohortPartitionReceipt?,
                ConductorAcceptanceCohortAttributionVerdict> applyVerdict)
    {
        if (store is null) return [];
        var consumed = new List<(AcceptanceCohortReceipt, IReadOnlyList<AcceptanceCohortPartitionReceipt>, IReadOnlySet<GoalId>)>();
        foreach (var receipt in store.ReadUnconsumedTerminalFailedReceipts())
        {
            if (!store.TryRecordOutcomeConsumption(receipt.Identity.Value, receipt.ReceiptId)) continue;
            var partitions = store.ReadPartitionReceipts(receipt.Identity.Value);
            var recorded = new HashSet<GoalId>();
            foreach (var member in receipt.AttributedMembers)
            {
                var goal = goals.SingleOrDefault(goal => goal.Id == member.GoalId);
                // Deleted goals have no lifecycle state to record or route.
                if (goal is null) continue;
                var partition = partitions.SingleOrDefault(partition => partition.GoalId == goal.Id);
                if (applyVerdict(receipt, goal, partition).ShouldRecord)
                    recorded.Add(goal.Id);
            }
            consumed.Add((receipt, partitions, recorded));
        }
        return consumed;
    }

    internal static IReadOnlyList<Goal> FilterEligible(IReadOnlyList<Goal> eligible,
        IReadOnlyList<(AcceptanceCohortReceipt Receipt, IReadOnlyList<AcceptanceCohortPartitionReceipt> Partitions,
            IReadOnlySet<GoalId> RecordedGoalIds)> consumed, int tick, Action<string> emit)
    {
        foreach (var (receipt, partitions, recordedGoalIds) in consumed)
        {
            emit(ConductorDriver.FormatTerminalFailedCohortReceipt(tick, receipt, partitions));
            eligible = eligible.Where(goal => !recordedGoalIds.Contains(goal.Id)).ToArray();
        }
        return eligible;
    }
}
