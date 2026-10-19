using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainEvidenceRetention
{
    internal static readonly TimeSpan MinimumAge = StorageRetentionMaintenance.AcceptanceArtifactMaxAge;

    internal static void Sweep(
        string orchestratorDirectory, IReadOnlyCollection<StorageRetentionGoal> goals,
        DateTimeOffset now, List<EvidenceRetentionDecision> decisions)
    {
        var root = Path.GetFullPath(Path.Combine(orchestratorDirectory, "merge-train-evidence"));
        var storePath = Path.Combine(orchestratorDirectory, "merge-train-acceptance.db");
        if (!Directory.Exists(root) || !File.Exists(storePath) || !goals.Any(goal => !goal.IsStoreStandIn)) return;

        var receipts = new Dictionary<string, MergeTrainReceipt>(StringComparer.Ordinal);
        try
        {
            var store = MergeTrainAcceptanceStore.OpenReadOnly(storePath);
            foreach (var receipt in store.ReadReceiptsSince(DateTimeOffset.MinValue))
                receipts.TryAdd(receipt.Identity.Value, receipt);
        }
        // Malformed or legacy payloads also leave all evidence intact and let later sweep families run.
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SqliteException
            or JsonException or ArgumentException)
        {
            decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.MergeTrainEvidence,
                EvidenceRetentionAction.RetainedUndecidable, root, null, EvidenceOwnerResolution.Unrecorded,
                "merge-train-store-unavailable", FailureExceptionType: ex.GetType().Name));
            return;
        }

        var goalsById = goals.ToLookup(goal => goal.GoalId, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in TrainDirectories(root))
        {
            var candidates = TrxFiles(directory);
            if (candidates.Length == 0) continue;

            if (!receipts.TryGetValue(Path.GetFileName(directory), out var receipt))
            {
                Keep(directory, EvidenceRetentionAction.RetainedUndecidable,
                    EvidenceOwnerResolution.Unrecorded, "merge-train-receipt-not-recorded", decisions);
                continue;
            }
            if (receipt.Outcome == MergeTrainGateOutcome.Passed && receipt.ValidForLanding && receipt.GateExitCode == 0)
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.Unrecorded, "merge-train-landing-evidence", decisions);
                continue;
            }
            if (now - receipt.CompletedAt <= MinimumAge)
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.Unrecorded, "merge-train-within-age", decisions);
                continue;
            }

            var memberGoals = receipt.Identity.Members.Select(member => goalsById[member.GoalId.Value].ToArray()).ToArray();
            if (memberGoals.Any(matches => matches.Length != 1 || matches[0].IsStoreStandIn))
            {
                Keep(directory, EvidenceRetentionAction.RetainedUndecidable,
                    EvidenceOwnerResolution.Unmatched, "merge-train-member-unresolved", decisions);
                continue;
            }
            if (memberGoals.Any(matches => !StorageRetentionMaintenance.IsReclaimableTerminalStatus(matches[0].Status)))
            {
                Keep(directory, EvidenceRetentionAction.Preserved,
                    EvidenceOwnerResolution.NonTerminal, "merge-train-member-not-terminal", decisions);
                continue;
            }

            foreach (var path in candidates)
            {
                var length = StorageRetentionMaintenance.SafeLength(path);
                var deletion = StorageRetentionMaintenance.TryDeleteExclusive(path);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.MergeTrainEvidence,
                    deletion.Success ? EvidenceRetentionAction.Deleted : EvidenceRetentionAction.DeferredLocked,
                    path, null, EvidenceOwnerResolution.UniqueTerminal,
                    deletion.Success ? "merge-train-red-trx-past-age" : "merge-train-trx-locked",
                    BytesAttempted: length, BytesReclaimed: deletion.Success ? length : 0,
                    FailureExceptionType: deletion.ExceptionType));
            }
        }
    }

    private static void Keep(string path, EvidenceRetentionAction action, EvidenceOwnerResolution owner,
        string reason, List<EvidenceRetentionDecision> decisions) =>
        decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.MergeTrainEvidence,
            action, path, null, owner, reason));

    private static string[] TrainDirectories(string root)
    {
        try
        {
            return new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(directory => directory.Name, StringComparer.Ordinal)
                .Select(directory => directory.FullName).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] TrxFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".trx", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
