using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable fixture rows and fixed timestamps; no shared state.
public sealed class ModelOutcomeScorecardClassMismatchTests
{
    [Fact]
    public void EveryFailedClassIsCountedWithoutReclassification()
    {
        var rows = new[]
        {
            Row(0, TaskOutcomeClass.RealFailure),
            Row(1, TaskOutcomeClass.Environmental),
            Row(2, TaskOutcomeClass.ManufacturedFixed),
            Row(3, TaskOutcomeClass.UnknownEra),
            Row(4, TaskOutcomeClass.Success, "committed-worker-result-evidence"),
            Row(5, TaskOutcomeClass.Success, "verified-no-change-round"),
            Row(6, TaskOutcomeClass.ReconciledToSuccess)
        };

        var record = Assert.Single(ModelOutcomeScorecard.Build(rows));

        Assert.Equal(0, record.Completed);
        Assert.Equal(7, record.Failed);
        Assert.Equal(1, record.RealFailures);
        Assert.Equal(1, record.EnvironmentalFailures);
        Assert.Equal(1, record.ManufacturedFixedFailures);
        Assert.Equal(1, record.UnknownEraFailures);
        Assert.Equal(3, record.ClassMismatchFailures);
        Assert.Equal("committed-worker-result-evidence=1,none=1,verified-no-change-round=1", record.ClassMismatchRules);
        Assert.Equal(record.Failed, record.RealFailures + record.EnvironmentalFailures +
            record.ManufacturedFixedFailures + record.UnknownEraFailures + record.ClassMismatchFailures);
        Assert.Equal(ModelOutcomeRecommendation.Neutral, record.Recommendation);
        Assert.Equal("Mixed outcomes: 1/7 real/code failure(s); 1 environmental failure(s); " +
            "1 manufactured-fixed failure(s); 1 unknown-era failure(s).", record.Reason);
        Assert.Equal(TaskOutcomeClass.Success, rows[4].OutcomeClass);
        Assert.Equal(TaskOutcomeClass.ReconciledToSuccess, rows[6].OutcomeClass);
    }

    [Fact]
    public void RulesAreCountedCaseInsensitivelyAndOrderedByCountThenName()
    {
        var rows = new[]
        {
            Row(0, TaskOutcomeClass.Success, "VERIFIED-NO-CHANGE-ROUND"),
            Row(1, TaskOutcomeClass.ReconciledToSuccess, "verified-no-change-round"),
            Row(2, TaskOutcomeClass.Success, "committed-worker-result-evidence"),
            Row(3, TaskOutcomeClass.Success, " "),
            Row(4, TaskOutcomeClass.Success, "ignored-completed") with { Outcome = WorkTaskStatus.Completed },
            Row(5, TaskOutcomeClass.RealFailure, "ignored-real")
        };

        var record = Assert.Single(ModelOutcomeScorecard.Build(rows));

        Assert.Equal(4, record.ClassMismatchFailures);
        Assert.Equal("verified-no-change-round=2,committed-worker-result-evidence=1,none=1", record.ClassMismatchRules);
    }

    [Fact]
    public void MismatchCountersRespectTheExistingWindowAndLaneGrouping()
    {
        var rows = new[]
        {
            Row(0, TaskOutcomeClass.Success),
            Row(1, TaskOutcomeClass.RealFailure),
            Row(2, TaskOutcomeClass.Success) with { DispatchLane = "other" }
        };

        var records = ModelOutcomeScorecard.Build(rows, windowSize: 1);

        Assert.Equal(2, records.Count);
        var main = Assert.Single(records.Where(record => record.DispatchLane == "codex-cli"));
        Assert.Equal(1, main.Failed);
        Assert.Equal(1, main.RealFailures);
        Assert.Equal(0, main.ClassMismatchFailures);
        Assert.Equal("", main.ClassMismatchRules);
        Assert.Equal(1, Assert.Single(records.Where(record => record.DispatchLane == "other")).ClassMismatchFailures);
    }

    private static ModelFitHistoryRow Row(int index, TaskOutcomeClass outcomeClass, string? rule = null) =>
        new("goal", $"task-{index}", AgentRole.Developer, "OpenAI", "gpt-6.1-sol", null, null,
            WorkTaskStatus.Failed, ModelFitHistory.Unknown,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index),
            rule, outcomeClass, "codex-cli");
}
