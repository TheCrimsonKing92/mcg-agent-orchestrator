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
                long bytes;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Delete))
                {
                    bytes = input.Length;
                    var expectedHash = SHA256.HashData(input);
                    input.Position = 0;
                    if (!File.Exists(destination))
                    {
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
                            input.CopyTo(gzip);
                    }
                    var compressed = File.Exists(destination) ? destination : temporary;
                    using (var verification = new GZipStream(File.OpenRead(compressed), CompressionMode.Decompress))
                    {
                        if (!SHA256.HashData(verification).AsSpan().SequenceEqual(expectedHash))
                            throw new InvalidDataException("Compressed journal failed content verification.");
                    }
                    if (compressed == temporary)
                    {
                        File.Move(temporary, destination);
                        published = true;
                    }
                    if (input.Length != bytes)
                        throw new InvalidDataException("Compressed journal failed content verification.");
                    File.Delete(source);
                }
                decisions.Add(new EvidenceRetentionDecision(EvidenceArtifactFamily.GoalOperationJournals,
                    EvidenceRetentionAction.Compressed, source, goalId,
                    EvidenceOwnerResolution.UniqueTerminal, "terminal-goal-journal-compressed",
                    BytesAttempted: bytes, BytesReclaimed: Math.Max(0, bytes - new FileInfo(destination).Length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
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
