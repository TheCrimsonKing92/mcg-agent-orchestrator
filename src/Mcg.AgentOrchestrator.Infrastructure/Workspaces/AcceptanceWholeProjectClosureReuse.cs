namespace Mcg.AgentOrchestrator.Infrastructure;

// Only deterministic core whole-project verdicts may cross candidate trees in this slice.
internal sealed class AcceptanceWholeProjectClosureReuse(AcceptanceClosureVerdictIndex index)
{
    internal static bool AppliesTo(string partitionId) =>
        partitionId.Equals("core-tests", StringComparison.Ordinal);

    internal AcceptanceClosureVerdictRecord? FindGreen(
        string manifestIdentity, string checkIdentity, string closureHash) =>
        index.FindLatest(manifestIdentity, checkIdentity, closureHash);

    internal static bool IsIndexable(
        PartitionVerdictRecord record, IReadOnlySet<string> retriedPartitionIds) =>
        record.IdenticalTree && AppliesTo(record.PartitionId) && record.Passed &&
        record.VerdictSource != "remote_first_run" && !record.ProbeRan &&
        !string.IsNullOrWhiteSpace(record.ClosureHash) &&
        !retriedPartitionIds.Contains(record.PartitionId);

    internal void AppendGreen(
        string manifestIdentity,
        IEnumerable<PartitionVerdictRecord> records,
        IReadOnlySet<string> retriedPartitionIds)
    {
        foreach (var record in records.Where(record => IsIndexable(record, retriedPartitionIds)))
            index.AppendGreen(manifestIdentity, record.PartitionFilterHash, record.ClosureHash!,
                record.AttemptId, record.TestResultPaths);
    }
}
