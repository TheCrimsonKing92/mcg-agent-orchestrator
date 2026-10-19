using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LandedMergeTrainEvidenceRetention
{
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromDays(3);

    internal static void Sweep(
        string orchestratorDirectory, IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now, List<EvidenceRetentionDecision> decisions)
    {
        var root = Path.GetFullPath(Path.Combine(orchestratorDirectory, "merge-train-evidence"));
        var storePath = Path.Combine(orchestratorDirectory, "merge-train-acceptance.db");
        if (!Directory.Exists(root) || !File.Exists(storePath) || !goals.Any(goal => !goal.IsStoreStandIn)) return;

        var receipts = new Dictionary<string, MergeTrainReceipt>(StringComparer.Ordinal);
        IReadOnlySet<string> finalized;
        try
        {
            var store = MergeTrainAcceptanceStore.OpenReadOnly(storePath);
            foreach (var receipt in store.ReadReceiptsSince(DateTimeOffset.MinValue))
                receipts.TryAdd(receipt.Identity.Value, receipt);
            finalized = store.ReadFinalizedLandingTrainIds();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SqliteException
            or JsonException or ArgumentException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.MergeTrainEvidence,
                EvidenceRetentionAction.RetainedUndecidable, root, null, EvidenceOwnerResolution.Unrecorded,
                "landed-merge-train-store-unavailable", FailureExceptionType: ex.GetType().Name));
            return;
        }

        var goalsById = goals.ToLookup(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
            .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(directory => directory.Name, StringComparer.Ordinal))
        {
            if (!receipts.TryGetValue(directory.Name, out var receipt)
                || receipt.Outcome != MergeTrainGateOutcome.Passed || !receipt.ValidForLanding || receipt.GateExitCode != 0
                || !finalized.Contains(directory.Name) || now - receipt.CompletedAt <= MinimumAge)
                continue;

            var memberGoals = receipt.Identity.Members.Select(member => goalsById[member.GoalId.Value].ToArray()).ToArray();
            if (memberGoals.Any(matches => matches.Length != 1 || matches[0].IsStoreStandIn
                || !StorageRetentionMaintenance.IsReclaimableTerminalStatus(matches[0].Status)))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory.FullName, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".trx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                var length = StorageRetentionMaintenance.SafeLength(path);
                var deletion = StorageRetentionMaintenance.TryDeleteExclusive(path);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.MergeTrainEvidence,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    path, null, EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "merge-train-landed-trx-past-age" : "merge-train-trx-locked",
                    BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
            }
        }
    }
}
