using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A genuine failed verdict remains evidence even when the bisect cannot execute.
internal static class MergeTrainBisectRedRetention
{
    internal static bool RecordsBeforeBisect(
        MergeTrainReceipt receipt, int memberCount, int attempt, MergeTrainMemberBinding? attributed) =>
        attempt == 0 && memberCount > ConductorMergeTrainSelector.MinimumCompositionMembers &&
        attributed is not null && MergeTrainRedAttribution.IsGenuineTrainRed(receipt, attributed);

    internal static bool KeepsFailedReceipt(MergeTrainReceipt? receipt) =>
        receipt?.Outcome == MergeTrainGateOutcome.Failed;
}
