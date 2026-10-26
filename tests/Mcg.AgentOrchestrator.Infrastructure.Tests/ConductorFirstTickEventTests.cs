using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorFirstTickEventTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 23, 3, 5, TimeSpan.Zero);

    [Fact]
    public void Observe_KnownStart_EmitsExactElapsedOnce()
    {
        var lines = new List<string>();
        var firstTick = new ConductorFirstTickEvent(() => Start, () => Start.AddSeconds(83), lines.Add);

        firstTick.Observe();
        firstTick.Observe();

        Assert.Equal(["STARTUP_FIRST_TICK elapsed_ms=83000 process_started=2026-10-25T23:03:05.0000000+00:00"], lines);
    }

    [Fact]
    public void Observe_UnknownStart_EmitsExactUnknownOnce()
    {
        var lines = new List<string>();
        var firstTick = new ConductorFirstTickEvent(() => null,
            () => throw new InvalidOperationException("Unknown start must not read the clock"), lines.Add);

        firstTick.Observe();
        firstTick.Observe();

        Assert.Equal(["STARTUP_FIRST_TICK elapsed_ms=unknown process_started=unknown"], lines);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(19_999, 1)]
    public void Observe_ElapsedTicks_ClampsAndTruncates(long ticks, long milliseconds)
    {
        var lines = new List<string>();
        var firstTick = new ConductorFirstTickEvent(() => Start, () => Start.AddTicks(ticks), lines.Add);

        firstTick.Observe();

        Assert.Equal($"STARTUP_FIRST_TICK elapsed_ms={milliseconds} process_started=2026-10-25T23:03:05.0000000+00:00", Assert.Single(lines));
    }

    [Fact]
    public void Observe_EmitThrows_ConsumesAttemptAndSwallowsFailure()
    {
        var attempts = 0;
        var firstTick = new ConductorFirstTickEvent(() => Start, () => Start, _ =>
        {
            attempts++;
            throw new IOException("injected emit failure");
        });

        Assert.Null(Record.Exception(firstTick.Observe));
        Assert.Null(Record.Exception(firstTick.Observe));
        Assert.Equal(1, attempts);
    }
}
