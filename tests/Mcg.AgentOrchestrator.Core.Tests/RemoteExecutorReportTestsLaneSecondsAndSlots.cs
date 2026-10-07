using Mcg.AgentOrchestrator.Core;

// Pure report inputs and fixed timestamps; safe to run in parallel.
public sealed class RemoteExecutorReportTestsLaneSecondsAndSlots
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, int> Slots =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["one"] = 2, ["zero"] = 1 };

    private static RemoteExecutorOutcomeRow[] Rows() =>
    [
        new(Start.AddMinutes(3), "one", "A", "lane", "accepted", null, 180, Start),
        new(Start.AddMinutes(4.5), "one", "B", "lane", "accepted", null, 210, Start.AddMinutes(1)),
        new(Start.AddMinutes(13), "one", "C", "lane", "accepted", null, 175, Start.AddMinutes(10)),
        new(Start.AddMinutes(30), "one", "D", "lane", "accepted", null, 190),
        new(Start.AddMinutes(20.5), "one", "E", "other", "unreachable", null, null, Start.AddMinutes(20))
    ];

    [Xunit.Fact]
    public void SlotsConcurrencyAndAcceptedLaneSecondsIncludeAllWindowedAttempts()
    {
        var report = RemoteExecutorReport.Build(["zero", "one"], Rows(), [], configuredSlots: Slots);
        var one = report.Executors[0];
        Assert.Equal(2, one.ConfiguredSlots);
        Assert.Equal(5, one.Attempts);
        Assert.Equal(2, one.PeakConcurrentAttempts);
        Assert.Equal(2, one.ConcurrentAttempts);
        Assert.Equal(new RemoteExecutorLaneSeconds("lane", 4, 185, 190, 175, 195), Assert.Single(one.Lanes));
        var zero = report.Executors[1];
        Assert.Equal(1, zero.ConfiguredSlots);
        Assert.Equal(0, zero.PeakConcurrentAttempts);
        Assert.Equal(0, zero.ConcurrentAttempts);
        Assert.Empty(zero.Lanes);
    }

    [Xunit.Fact]
    public void SinceFiltersBeforeConcurrencyAndSampleSplits()
    {
        var report = RemoteExecutorReport.Build(["zero", "one"], Rows(), [],
            since: Start.AddMinutes(3.5), configuredSlots: Slots);
        var one = report.Executors[0];
        Assert.Equal(1, one.PeakConcurrentAttempts);
        Assert.Equal(0, one.ConcurrentAttempts);
        Assert.Equal(new RemoteExecutorLaneSeconds("lane", 3, 190, 190, 192.5, null), Assert.Single(one.Lanes));
    }

    [Xunit.Fact]
    public void PeakCountsSimultaneousIntervalsRatherThanAllNeighboursOfOneAttempt()
    {
        var rows = new RemoteExecutorOutcomeRow[]
        {
            new(Start.AddMinutes(10), "one", "long", "z", "accepted", null, 10, Start),
            new(Start.AddMinutes(2), "one", "first", "a", "unreachable", null, null, Start.AddMinutes(1)),
            new(Start.AddMinutes(4), "one", "second", "a", "accepted", null, 4, Start.AddMinutes(3)),
            new(Start.AddMinutes(12), "one", "touching", "a", "accepted", null, 2, Start.AddMinutes(10)),
            new(Start.AddMinutes(13), "one", "excluded", "a", "late-after-fallback", null, 99, Start),
            new(Start.AddMinutes(14), "one", "inverted", "a", "accepted", null, 6, Start.AddMinutes(15))
        };
        var one = Assert.Single(RemoteExecutorReport.Build([], rows, [], last: 1).Executors);
        Assert.Null(one.ConfiguredSlots);
        Assert.Equal(5, one.Attempts);
        Assert.Equal(2, one.PeakConcurrentAttempts);
        Assert.Equal(3, one.ConcurrentAttempts);
        Assert.Equal(new[] { "a", "z" }, one.Lanes.Select(lane => lane.Lane));
        Assert.Equal(new RemoteExecutorLaneSeconds("a", 3, 4, 6, 2, 4), one.Lanes[0]);
        Assert.Equal(new RemoteExecutorLaneSeconds("z", 1, 10, 10, null, 10), one.Lanes[1]);
        Assert.Single(one.RecentAttempts);
    }
}
