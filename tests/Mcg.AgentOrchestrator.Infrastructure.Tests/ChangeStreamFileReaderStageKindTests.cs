using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its stream and reader cursor.
public sealed class ChangeStreamFileReaderStageKindTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-stage-reader-");

    [Fact]
    public void ReadAvailable_MixedKindsRemainOrderedAndTailExactlyOnce()
    {
        var path = Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
        var reader = new ChangeStreamFileReader(path);
        var writer = new ChangeStreamWriter(path);
        var timestamp = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var expected = new[]
        {
            Assert.IsType<ChangeStreamRecord>(writer.Append("goal", "g", "active", timestamp)),
            Assert.IsType<ChangeStreamRecord>(writer.Append("goal-lifecycle", "g", "TaskCompleted task=t1", timestamp)),
            Assert.IsType<ChangeStreamRecord>(writer.Append("goal-escalation", "g", "owner needed", timestamp)),
            Assert.IsType<ChangeStreamRecord>(writer.Append("acceptance", "g", "passed", timestamp))
        };
        var records = reader.ReadAvailable(out var discontinuity);
        Assert.Null(discontinuity);
        Assert.Equal(expected, records);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, records.Select(record => record.Sequence));
        Assert.Equal(new[] { ChangeStreamRecord.GoalTransition, ChangeStreamRecord.GoalStage,
            ChangeStreamRecord.OwnerDecisionRaised, ChangeStreamRecord.GoalStage }, records.Select(record => record.ChangeKind));

        var appended = writer.Append("goal-lifecycle", "g", "TaskVerified task=t1", timestamp);
        Assert.NotNull(appended);
        Assert.Equal(appended, Assert.Single(reader.ReadAvailable(out discontinuity)));
        Assert.Null(discontinuity);
        Assert.Empty(reader.ReadAvailable(out discontinuity));
        Assert.Null(discontinuity);
    }

    public void Dispose() => _root.Delete(true);
}
