public sealed class ConsoleHostCensusTests
{
    [Fact]
    public void Count_UnseenMember_RemainsGapInsteadOfConhost()
    {
        var counts = ConsoleHostCensus.Count(3, [false, true]);

        Assert.Equal(1, counts.NonConhost);
        Assert.Equal(1, counts.Conhost);
        Assert.Equal(1, counts.Unclassified);
    }

    [Fact]
    public void Count_CompleteMixedCensus_HasNoGap()
    {
        var counts = ConsoleHostCensus.Count(2, [false, true]);

        Assert.Equal(1, counts.NonConhost);
        Assert.Equal(1, counts.Conhost);
        Assert.Equal(0, counts.Unclassified);
    }

    [Fact]
    public void Count_OnlyNonConhost_HasNoConhostOrGap()
    {
        var counts = ConsoleHostCensus.Count(1, [false]);

        Assert.Equal(1, counts.NonConhost);
        Assert.Equal(0, counts.Conhost);
        Assert.Equal(0, counts.Unclassified);
    }

    [Fact]
    public Task MeasureAsync_GapThenComplete_ReturnsSecondMeasurementWithOneRetake() =>
        AssertMeasurementSequence([1, 0], expectedCalls: 2, expectedRetakes: 1);

    [Fact]
    public Task MeasureAsync_PersistentGap_ReturnsThirdMeasurementWithGapAndTwoRetakes() =>
        AssertMeasurementSequence([1, 1, 1], expectedCalls: 3, expectedRetakes: 2);

    [Fact]
    public Task MeasureAsync_CompleteCensus_ReturnsFirstMeasurementWithoutRetake() =>
        AssertMeasurementSequence([0], expectedCalls: 1, expectedRetakes: 0);

    [Fact]
    public async Task MeasureAsync_MeasureThrows_PropagatesWithoutRetake()
    {
        var calls = 0;
        var failure = new InvalidOperationException("Measurement failed.");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ConsoleHostCensus.MeasureAsync(
            () =>
            {
                calls++;
                return Task.FromException<Measurement>(failure);
            }, measurement => measurement.Unclassified, ConsoleHostCensus.MaxAttempts));

        Assert.Same(failure, exception);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MeasureAsync_InvalidAttemptLimit_RejectsBeforeMeasurement(int maxAttempts)
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConsoleHostCensus.MeasureAsync(
            () =>
            {
                calls++;
                return Task.FromResult(new Measurement(0));
            }, measurement => measurement.Unclassified, maxAttempts));

        Assert.Equal("maxAttempts", exception.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MeasureAsync_NullDelegates_RejectsArguments()
    {
        var measureException = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ConsoleHostCensus.MeasureAsync<Measurement>(null!, measurement => measurement.Unclassified, 3));
        var selectorException = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ConsoleHostCensus.MeasureAsync(() => Task.FromResult(new Measurement(0)), null!, 3));

        Assert.Equal("measure", measureException.ParamName);
        Assert.Equal("unclassified", selectorException.ParamName);
    }

    private static async Task AssertMeasurementSequence(int[] sequence, int expectedCalls, int expectedRetakes)
    {
        var measurements = sequence.Select(unclassified => new Measurement(unclassified)).ToArray();
        var calls = new List<Measurement>();
        var result = await ConsoleHostCensus.MeasureAsync(() =>
        {
            var measurement = measurements[calls.Count];
            calls.Add(measurement);
            return Task.FromResult(measurement);
        }, measurement => measurement.Unclassified, ConsoleHostCensus.MaxAttempts);

        Assert.Equal(3, ConsoleHostCensus.MaxAttempts);
        Assert.Equal(expectedCalls, calls.Count);
        Assert.Equal(sequence[..expectedCalls], calls.Select(measurement => measurement.Unclassified));
        Assert.Same(measurements[expectedCalls - 1], result.Measurement);
        Assert.Equal(sequence[expectedCalls - 1], result.Measurement.Unclassified);
        Assert.Equal(expectedRetakes, result.Retakes);
    }

    private sealed record Measurement(int Unclassified);
}
