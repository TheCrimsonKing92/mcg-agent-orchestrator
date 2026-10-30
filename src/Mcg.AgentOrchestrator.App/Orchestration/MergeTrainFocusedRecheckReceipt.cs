using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainFocusedRecheckReceipt
{
    internal const string Prefix = "merge-train-recheck-receipt-";

    internal static MergeTrainReceipt Compose(MergeTrainFocusedRecheckDecision decision,
        MergeTrainIdentity identity, MergeTrainFocusedRecheckRunner.RunResult run, DateTimeOffset now)
    {
        var selection = decision.Selection!.Identities.ToArray();
        var provenance = new MergeTrainRecheckProvenance(decision.RedReceipt.Identity.Value,
            decision.RedReceipt.ReceiptId, decision.TrainAttemptId, run.AttemptId,
            decision.Culprit.GoalId, decision.AttributionSource, selection, true,
            decision.DependencyClasses.ToArray(), true, run.MainRevision,
            run.Verification.TreeRevision!, selection, run.NotRunSelectionCount);
        return new(ReceiptId(identity, provenance), identity, MergeTrainGateOutcome.Passed, now,
            run.Verification.ElapsedMilliseconds, [], 0, run.Verification.TestResultPaths, ValidForLanding: true)
        { Recheck = provenance, GateAttemptId = run.AttemptId };
    }

    internal static string ReceiptId(MergeTrainIdentity identity, MergeTrainRecheckProvenance provenance) =>
        Prefix + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Identity = identity.Value, Provenance = provenance })));
}
