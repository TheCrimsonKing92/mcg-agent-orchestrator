using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every batch operation and its observations are local to the test.
public sealed class ArchivedSnapshotCompressionCadenceTests
{
    [Fact]
    public void Run_DrainedBatches_SumsResultsAndCountsEmptyCall()
    {
        var budgets = new List<long>();
        var batches = new Queue<GoalSnapshotArchiveRecompressionResult>(
            [new(3, 300, 20), new(2, 200, 15), new(0, 0, 0)]);
        var result = ArchivedSnapshotCompressionCadence.Run(bytes =>
        {
            budgets.Add(bytes);
            return batches.Dequeue();
        });
        Assert.Equal(new(5, 500, 35, 3, "completed", null), result);
        Assert.Equal(3, budgets.Count);
        Assert.All(budgets, bytes => Assert.Equal(67_108_864L, bytes));
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=completed rows=5 sourceBytes=500 storedBytes=35 batches=3",
            ArchivedSnapshotCompressionCadence.Format(result));
        Assert.DoesNotContain("GOAL_SNAPSHOT_ARCHIVE", ArchivedSnapshotCompressionCadence.Format(result));
    }

    [Fact]
    public void Run_NeverDrained_StopsAfter32Calls()
    {
        var calls = 0;
        var result = ArchivedSnapshotCompressionCadence.Run(bytes =>
        {
            Assert.Equal(67_108_864L, bytes);
            calls++;
            return new(1, 10, 1);
        });
        Assert.Equal(32, calls);
        Assert.Equal(new(32, 320, 32, 32, "completed", null), result);
        Assert.DoesNotContain("GOAL_SNAPSHOT_ARCHIVE", ArchivedSnapshotCompressionCadence.Format(result));
    }

    [Fact]
    public void Run_SecondCallFails_KeepsPartialSumsAndCountsFailedCall()
    {
        var calls = 0;
        var result = ArchivedSnapshotCompressionCadence.Run(_ =>
            ++calls == 1 ? new(1, 10, 1) : throw new InvalidOperationException("fixture"));
        Assert.Equal(2, calls);
        Assert.Equal(new(1, 10, 1, 2, "failed", "InvalidOperationException"), result);
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=failed rows=1 sourceBytes=10 storedBytes=1 batches=2 exception=InvalidOperationException",
            ArchivedSnapshotCompressionCadence.Format(result));
        Assert.DoesNotContain("GOAL_SNAPSHOT_ARCHIVE", ArchivedSnapshotCompressionCadence.Format(result));
    }

    [Fact]
    public void Run_CanceledBatch_PropagatesCancellation()
    {
        var cancellation = new OperationCanceledException("fixture");
        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() =>
            ArchivedSnapshotCompressionCadence.Run(_ => throw cancellation)));
    }

    [Fact]
    public void Format_Disabled_ReportsZeroWork()
    {
        var line = ArchivedSnapshotCompressionCadence.Format(ArchivedSnapshotCompressionCadence.Disabled);
        Assert.Equal("ARCHIVED_SNAPSHOT_COMPRESSION status=disabled rows=0 sourceBytes=0 storedBytes=0 batches=0", line);
        Assert.DoesNotContain("GOAL_SNAPSHOT_ARCHIVE", line);
    }
}
