using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptancePartitionVerdictCachePrebuildReceiptTests
{
    [Theory]
    [InlineData(4321L, null, "4321")]
    [InlineData(1000L, 500L, "1500")]
    [InlineData(null, null, "not-run")]
    public void CompleteAttempt_ReportsRecordedPrebuildDurationOrNotRun(
        long? firstDuration, long? secondDuration, string expected)
    {
        using var context = new PartitionVerdictCacheMeasurementContext();
        var lane = context.Lane("Alpha");
        var cache = context.Create("attempt", [lane]);
        if (firstDuration is not null)
            cache.RecordSharedPrebuildDuration("attempt", firstDuration);
        if (secondDuration is not null)
            cache.RecordSharedPrebuildDuration("attempt", secondDuration);
        cache.RecordExecution(lane, context.Result(lane));

        var receipt = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.Contains($"shared_prebuild_duration_ms={expected}", receipt.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteAttempt_IgnoresOtherAttemptAndCountsUnknownDurationAsRun()
    {
        using var context = new PartitionVerdictCacheMeasurementContext();
        var lane = context.Lane("Alpha");
        var cache = context.Create("attempt", [lane]);
        cache.RecordSharedPrebuildDuration("other-attempt", 999);
        cache.RecordExecution(lane, context.Result(lane));
        var noPrebuild = Assert.IsType<AcceptanceCheckResult>(cache.CompleteAttempt());
        Assert.Contains("shared_prebuild_duration_ms=not-run", noPrebuild.ResultSummary, StringComparison.Ordinal);

        var later = context.Create("later", [lane]);
        later.RecordSharedPrebuildDuration("later", null);
        later.RecordExecution(lane, context.Result(lane));
        var unknownDuration = Assert.IsType<AcceptanceCheckResult>(later.CompleteAttempt());
        Assert.Contains("shared_prebuild_duration_ms=0", unknownDuration.ResultSummary, StringComparison.Ordinal);
    }
}
