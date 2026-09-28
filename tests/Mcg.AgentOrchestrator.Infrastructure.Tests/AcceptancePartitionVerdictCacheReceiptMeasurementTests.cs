using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCacheReceiptMeasurementTests
{
    [Fact]
    public void CompleteAttempt_ReportsMissesAndMeasuredExecutionSeparately()
    {
        using var context = new PartitionVerdictCacheMeasurementContext();
        var lanes = new[] { context.Lane("Alpha"), context.Lane("Beta"),
            context.Lane("Charlie"), context.Lane("Delta") };
        var first = context.Create("first", lanes);
        first.RecordExecution(lanes[0], context.Result(lanes[0], duration: 50));
        Assert.NotNull(first.CompleteAttempt());

        var attempt = context.Create("second", lanes);
        Assert.NotNull(attempt.TryReuse(lanes[0]));
        foreach (var lane in lanes.Skip(1))
            Assert.Null(attempt.TryReuse(lane));
        attempt.RecordExecution(lanes[1], context.Result(lanes[1], duration: 1200));
        attempt.RecordExecution(lanes[2], context.Result(lanes[2], duration: 800));

        var receipt = Assert.IsType<AcceptanceCheckResult>(attempt.CompleteAttempt());
        var detail = Assert.IsType<string>(receipt.ResultSummary);
        Assert.Contains("reused_lanes=1", detail, StringComparison.Ordinal);
        Assert.Contains("executed_lanes=2", detail, StringComparison.Ordinal);
        Assert.Contains("executed_lane_duration_ms=2000", detail, StringComparison.Ordinal);
        Assert.Contains("shared_prebuild_duration_ms=not-run", detail, StringComparison.Ordinal);
        Assert.Contains("missed_lane=beta:closure-hash-unavailable", detail, StringComparison.Ordinal);
        Assert.Contains("missed_lane=charlie:closure-hash-unavailable", detail, StringComparison.Ordinal);
        Assert.Contains("missed_lane=delta:closure-hash-unavailable", detail, StringComparison.Ordinal);
        Assert.Equal(3, detail.Split(' ').Count(token => token.StartsWith("missed_lane=", StringComparison.Ordinal)));
        Assert.DoesNotContain("before_reroll_wall_time=20-25m", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("after_reroll_wall_time=2-7m", detail, StringComparison.Ordinal);
        Assert.All(detail.Split(' ').Skip(1), token => Assert.Contains('=', token));

        var journalLine = Assert.Single(SharedJsonlFile.ReadAllLines(attempt.JournalPath),
            line => line.Contains("acceptance:partition-verdict-cache", StringComparison.Ordinal) &&
                line.Contains("second", StringComparison.Ordinal));
        using var journal = JsonDocument.Parse(journalLine);
        Assert.Equal(detail, journal.RootElement.GetProperty("detail").GetString());
    }
}
