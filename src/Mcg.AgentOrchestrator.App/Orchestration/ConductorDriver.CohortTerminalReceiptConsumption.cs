using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal IReadOnlyList<(AcceptanceCohortReceipt Receipt, IReadOnlyList<AcceptanceCohortPartitionReceipt> Partitions)>
        ConsumeTerminalFailedCohortReceipts()
    {
        if (_cohortAcceptanceStore is null) return [];
        var consumed = new List<(AcceptanceCohortReceipt, IReadOnlyList<AcceptanceCohortPartitionReceipt>)>();
        foreach (var receipt in _cohortAcceptanceStore.ReadUnconsumedTerminalFailedReceipts())
        {
            if (_cohortAcceptanceStore.TryRecordOutcomeConsumption(receipt.Identity.Value, receipt.ReceiptId))
            {
                consumed.Add((receipt, _cohortAcceptanceStore.ReadPartitionReceipts(receipt.Identity.Value)));
            }
        }
        return consumed;
    }

    internal IReadOnlySet<string> ReadInteractionOnlyMemberKeys() =>
        _cohortAcceptanceStore?.ReadInteractionOnlyMemberKeys() ?? new HashSet<string>(StringComparer.Ordinal);

    internal static string FormatTerminalFailedCohortReceipt(int tick, AcceptanceCohortReceipt receipt,
        IReadOnlyList<AcceptanceCohortPartitionReceipt> partitions)
    {
        var members = receipt.Identity.Members;
        var outcome = receipt.Attribution == AcceptanceCohortAttributionOutcome.InteractionOnly
            ? "interaction-only" : "failed";
        var partitionOutcomes = members.Select(member =>
            $"{member.GoalId.Value[..8]}:{partitions.FirstOrDefault(partition => partition.GoalId == member.GoalId)?.Outcome.ToString() ?? "none"}");
        return $"ACCEPTANCE_COHORT tick={tick} members={string.Join(',', members.Select(member => member.GoalId.Value[..8]))} " +
            $"outcome={outcome} receipt={receipt.ReceiptId} attribution={receipt.Attribution} partitions={string.Join(',', partitionOutcomes)}";
    }
}
