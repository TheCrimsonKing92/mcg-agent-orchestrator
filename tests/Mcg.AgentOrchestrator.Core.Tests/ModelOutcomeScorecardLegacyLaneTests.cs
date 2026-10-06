using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory history rows with fixed timestamps only.
public sealed class ModelOutcomeScorecardLegacyLaneTests
{
    [Fact]
    public void Build_LegacyAndCurrentCheapLanes_MergesOutcomeCounts()
    {
        var rows = new[]
        {
            Row("codex-spark:cascade-cheap", WorkTaskStatus.Completed, 0),
            Row("codex-spark:cascade-cheap", WorkTaskStatus.Failed, 1),
            Row("codex-luna:cascade-cheap", WorkTaskStatus.Completed, 2),
            Row("codex-luna:cascade-cheap", WorkTaskStatus.Failed, 3),
            Row("codex-luna:cascade-cheap", WorkTaskStatus.Completed, 4)
        };

        var record = Assert.Single(ModelOutcomeScorecard.Build(rows));

        Assert.Equal("codex-luna:cascade-cheap", record.DispatchLane);
        Assert.Equal(3, record.Completed);
        Assert.Equal(2, record.Failed);
    }

    [Theory]
    [InlineData("codex-spark", "codex-luna")]
    [InlineData("CODEX-SPARK:cascade-cheap", "codex-luna:cascade-cheap")]
    [InlineData("codex-cli", "codex-cli")]
    [InlineData("codex-sparkle", "codex-sparkle")]
    [InlineData(null, null)]
    public void Build_DispatchLane_NormalizesOnlyLegacyLane(string? input, string? expected)
    {
        var record = Assert.Single(ModelOutcomeScorecard.Build([Row(input, WorkTaskStatus.Completed, 0)]));
        Assert.Equal(expected, record.DispatchLane);
    }

    private static ModelFitHistoryRow Row(string? lane, WorkTaskStatus outcome, int index) => new(
        $"goal-{index}", $"task-{index}", AgentRole.Tester, "OpenAI", "gpt-6-luna",
        TaskComplexity.Simple, "focused verification", outcome, ModelFitHistory.Adequate,
        DateTimeOffset.Parse("2026-10-06T12:00:00Z").AddMinutes(index), DispatchLane: lane);
}
