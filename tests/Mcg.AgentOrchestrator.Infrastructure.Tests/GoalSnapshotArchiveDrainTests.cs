using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class GoalSnapshotArchiveDrainTests
{
    [Fact]
    public void Run_SeveralBatches_SumsCountsUntilEmptyBatch()
    {
        var drain = new GoalSnapshotArchiveDrain(123, 16);
        var counts = new Queue<int>([2, 2, 1, 0]);
        var arguments = new List<long>();

        var total = drain.Run(bytes =>
        {
            arguments.Add(bytes);
            return counts.Dequeue();
        });

        Assert.Equal(5, total);
        Assert.Equal(4, arguments.Count);
        Assert.All(arguments, bytes => Assert.Equal(123L, bytes));
        Assert.Empty(counts);
    }

    [Fact]
    public void Run_AlwaysNonempty_StopsAtBatchCap()
    {
        var drain = new GoalSnapshotArchiveDrain(123, 3);
        var calls = 0;

        var total = drain.Run(_ => { calls++; return 1; });

        Assert.Equal(3, total);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Run_FirstBatchEmpty_ReturnsZeroAfterOneCall()
    {
        var drain = new GoalSnapshotArchiveDrain(123, 16);
        var calls = 0;

        var total = drain.Run(_ => { calls++; return 0; });

        Assert.Equal(0, total);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonpositiveLimits_ThrowsWithParameterName(int value)
    {
        Assert.Equal("batchSnapshotBytes", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoalSnapshotArchiveDrain(value, 1)).ParamName);
        Assert.Equal("maxBatchesPerRun", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GoalSnapshotArchiveDrain(1, value)).ParamName);
    }

    [Fact]
    public void Run_SecondBatchThrows_PropagatesSameExceptionAndStops()
    {
        var drain = new GoalSnapshotArchiveDrain(123, 16);
        var exception = new InvalidOperationException("fixture batch failure");
        var calls = 0;

        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() => drain.Run(_ =>
        {
            if (++calls == 2)
                throw exception;
            return 1;
        })));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Default_Uses128MiBAndSixteenBatches()
    {
        Assert.Equal(134_217_728L, GoalSnapshotArchiveDrain.Default.BatchSnapshotBytes);
        Assert.Equal(16, GoalSnapshotArchiveDrain.Default.MaxBatchesPerRun);
    }
}
