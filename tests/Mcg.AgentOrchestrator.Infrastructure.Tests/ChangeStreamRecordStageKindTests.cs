using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure classification and serialization.
public sealed class ChangeStreamRecordStageKindTests
{
    [Theory]
    [InlineData("goal-lifecycle")]
    [InlineData("acceptance")]
    [InlineData("acceptance-cohort")]
    public void TryClassify_StageSourceRequiresGoalId(string source)
    {
        Assert.True(ChangeStreamRecord.TryClassify(source, "g", out var kind));
        Assert.Equal(ChangeStreamRecord.GoalStage, kind);
        foreach (var goalId in new string?[] { null, "", " " })
        {
            Assert.False(ChangeStreamRecord.TryClassify(source, goalId, out kind));
            Assert.Null(kind);
        }
    }

    [Theory]
    [InlineData("gate-progress")]
    [InlineData("canary-gate")]
    [InlineData("blocked-recheck-heartbeat")]
    [InlineData("goal-stalled")]
    public void TryClassify_HeartbeatSourceRemainsUntyped(string source)
    {
        Assert.False(ChangeStreamRecord.TryClassify(source, "g", out var kind));
        Assert.Null(kind);
    }

    [Theory]
    [InlineData("goal-lifecycle")]
    [InlineData("acceptance")]
    [InlineData("acceptance-cohort")]
    public void TryParse_SerializedStageRecordRoundTrips(string source)
    {
        var record = new ChangeStreamRecord(1, 7, DateTimeOffset.Parse("2026-10-10T12:00:00Z"),
            ChangeStreamRecord.GoalStage, "g", source, "TaskCompleted task=t1");
        Assert.Equal(1, ChangeStreamRecord.CurrentSchemaVersion);
        Assert.True(ChangeStreamRecord.TryParse(
            JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions), out var parsed));
        Assert.Equal(record, parsed);
    }
}
