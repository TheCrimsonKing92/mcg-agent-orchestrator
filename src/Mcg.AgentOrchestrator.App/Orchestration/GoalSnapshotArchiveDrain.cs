namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalSnapshotArchiveDrain
{
    internal static readonly GoalSnapshotArchiveDrain Default = new(134_217_728, 16);

    internal long BatchSnapshotBytes { get; }
    internal int MaxBatchesPerRun { get; }

    internal GoalSnapshotArchiveDrain(long batchSnapshotBytes, int maxBatchesPerRun)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSnapshotBytes, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchesPerRun, 1);
        BatchSnapshotBytes = batchSnapshotBytes;
        MaxBatchesPerRun = maxBatchesPerRun;
    }

    internal int Run(Func<long, int> archiveBatch)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var archived = archiveBatch(BatchSnapshotBytes);
            if (archived == 0)
                break;
            total += archived;
        }
        return total;
    }
}
