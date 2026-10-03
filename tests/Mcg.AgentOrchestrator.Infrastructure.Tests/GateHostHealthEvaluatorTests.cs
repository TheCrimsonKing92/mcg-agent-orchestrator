using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the evaluator consumes in-memory records only.
public sealed class GateHostHealthEvaluatorTests
{
    [Theory]
    [InlineData(true, 3, new double[] { 60, 55, 70, 58, 62, 200, 210, 230 })]
    [InlineData(false, 1, new double[] { 60, 55, 70, 58, 62, 200, 90, 230 })]
    [InlineData(false, 2, new double[] { 60, 55, 200, 230 })]
    public void RequiresBaselineAndConsecutiveDegradedTail(bool degraded, int consecutive, double[] launches)
    {
        var assessment = GateHostHealthEvaluator.Evaluate(Records(launches));
        Assert.Equal(degraded, assessment.IsDegraded);
        Assert.Equal(55d, assessment.BaselineMs);
        Assert.Equal(consecutive, assessment.ConsecutiveCount);
        Assert.Equal(launches.Length, assessment.RecordCount);
        Assert.Equal(230d, assessment.LatestLaunchMs);
        Assert.Equal(11800d, assessment.LatestPagedPoolMb);
        Assert.Equal(230d / 55, assessment.Ratio);
    }

    [Fact]
    public void ThresholdIsInclusiveAndOldBaselineExpiresWithWindow()
    {
        var exact = GateHostHealthEvaluator.Evaluate(Records([55, 60, 165, 165, 165]));
        Assert.True(exact.IsDegraded);
        Assert.Equal(3d, exact.Ratio);
        var expired = GateHostHealthEvaluator.Evaluate(Records(
            new[] { 1d }.Concat(Enumerable.Repeat(60d, GateHostHealthEvaluator.RetainedRecordWindow)).ToArray()));
        Assert.False(expired.IsDegraded);
        Assert.Equal(60d, expired.BaselineMs);
        Assert.Equal(GateHostHealthEvaluator.RetainedRecordWindow, expired.RecordCount);
    }

    [Fact]
    public void EmptyLedgerIsHealthyWithoutMeasurements()
    {
        var assessment = GateHostHealthEvaluator.Evaluate([]);
        Assert.False(assessment.IsDegraded);
        Assert.Null(assessment.BaselineMs);
        Assert.Null(assessment.LatestLaunchMs);
        Assert.Equal(0, assessment.ConsecutiveCount);
    }

    private static HostHealthLedgerRecord[] Records(double[] launches) => launches.Select((launch, index) =>
        new HostHealthLedgerRecord(DateTimeOffset.UnixEpoch.AddMinutes(index), $"attempt-{index}", launch, 11800)).ToArray();
}
