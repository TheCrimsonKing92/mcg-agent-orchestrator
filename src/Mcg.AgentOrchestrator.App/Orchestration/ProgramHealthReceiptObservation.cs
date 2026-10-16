using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ProgramHealthReceiptObservation(string ReceiptId, DateTimeOffset PassedAt,
    string ObservedMainRevision, IReadOnlyList<string> MemberGoalIds)
{
    internal static ProgramHealthReceiptObservation FromMergeTrainReceipt(MergeTrainReceipt receipt) =>
        new(receipt.ReceiptId, receipt.CompletedAt, receipt.Identity.ObservedMainRevision,
            receipt.Identity.Members.Select(member => member.GoalId.Value).ToArray());
}
