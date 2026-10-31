using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MergeTrainReproductionOutcome(string MemberId, string Result, string? Reason,
    IReadOnlyList<string> SelectedTests, MergeTrainReproductionProvenance? Provenance)
{
    internal string LogLine(string receiptId) =>
        $"MERGE_TRAIN_REPRODUCTION receipt={receiptId} member={MemberId} outcome={Result} reason={Reason ?? "none"} evidence=" +
        JsonSerializer.Serialize(this);
}
