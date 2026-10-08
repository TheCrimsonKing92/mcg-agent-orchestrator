using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure record classification and serialization.
public sealed class ChangeStreamRecordTests
{
    [Theory]
    [InlineData("goal", "g", ChangeStreamRecord.GoalTransition)]
    [InlineData("goal-landing", "g", ChangeStreamRecord.GoalTransition)]
    [InlineData("goal-left-working-set", "g", ChangeStreamRecord.GoalTransition)]
    [InlineData("watch-transition", "g", ChangeStreamRecord.GoalTransition)]
    [InlineData("goal-escalation", "g", ChangeStreamRecord.OwnerDecisionRaised)]
    [InlineData("blocked-recheck-heartbeat", "g", null)]
    [InlineData("acceptance-lease", "g", null)]
    [InlineData("goal-stalled", "g", null)]
    [InlineData("goal", null, null)]
    [InlineData("goal-escalation", " ", null)]
    public void Classify_UsesOnlySupportedKindsWithGoal(string kind, string? goal, string? expected)
    {
        Assert.Equal(expected is not null, ChangeStreamRecord.TryClassify(kind, goal, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Serialize_RoundTripsEveryField()
    {
        var record = new ChangeStreamRecord(1, 42, DateTimeOffset.Parse("2026-10-08T12:00:00Z"),
            ChangeStreamRecord.GoalTransition, "goal-id", "goal", "detail");
        var json = JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(42, document.RootElement.GetProperty("sequence").GetInt64());
        Assert.True(ChangeStreamRecord.TryParse(json, out var actual));
        Assert.Equal(record, actual);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("null")]
    public void Parse_RejectsMalformedRecords(string line) =>
        Assert.False(ChangeStreamRecord.TryParse(line, out _));
}
