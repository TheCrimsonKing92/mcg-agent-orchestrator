using System.IO.Compression;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class StorageRetentionMaintenance
{
    private static void CompressTerminalGoalJournals(
        string executionDirectory, IReadOnlyCollection<StorageRetentionGoal> goals, DateTimeOffset now,
        StorageRetentionReclaimOptions options, List<EvidenceRetentionDecision> decisions)
    {
        if (options.TerminalJournalCompressionAge is null || !goals.Any(goal => !goal.IsStoreStandIn))
            return;
        foreach (var goalId in SelectGoalOperationPrunableIds(goals))
        {
            var source = GoalOperationJournal.PathFor(executionDirectory, new GoalId(goalId));
            if (!File.Exists(source)) continue;
            var destination = source + ".gz";
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            var published = false;
            try
            {
                if (now - File.GetLastWriteTimeUtc(source) <= MaxGrace(options.TerminalJournalCompressionAge.Value))
                {
                    decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.GoalOperationJournals,
                        EvidenceRetentionAction.Preserved, source, goalId,
                        EvidenceOwnerResolution.UniqueTerminal, "within-grace-period"));
                    continue;
                }
                if (File.Exists(destination))
                {
                    decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.GoalOperationJournals,
                        EvidenceRetentionAction.Preserved, source, goalId,
                        EvidenceOwnerResolution.UniqueTerminal, "journal-compressed-sibling-exists"));
                    continue;
                }
                long bytes;
                byte[] expectedHash;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bytes = input.Length;
                    expectedHash = SHA256.HashData(input);
                    input.Position = 0;
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
                    input.CopyTo(gzip);
                }
                using (var verification = new GZipStream(File.OpenRead(temporary), CompressionMode.Decompress))
                {
                    if (!SHA256.HashData(verification).AsSpan().SequenceEqual(expectedHash))
                        throw new InvalidDataException("Compressed journal failed content verification.");
                }
                File.Move(temporary, destination);
                published = true;
                File.Delete(source);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.GoalOperationJournals,
                    EvidenceRetentionAction.Compressed, source, goalId,
                    EvidenceOwnerResolution.UniqueTerminal, "terminal-goal-journal-compressed",
                    BytesAttempted: bytes, BytesReclaimed: Math.Max(0, bytes - new FileInfo(destination).Length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                if (published && File.Exists(source)) TryDelete(destination);
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.GoalOperationJournals,
                    EvidenceRetentionAction.DeferredLocked, source, goalId,
                    EvidenceOwnerResolution.UniqueTerminal, "journal-compression-failed",
                    FailureExceptionType: ex.GetType().Name));
            }
        }
    }
}
