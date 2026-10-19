using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Pure fixtures; safe to run in parallel.
public sealed class RemoteExecutorReportBlankIdTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void BlankIdsNeverCreateExecutorsAndSkipsAreCounted(string? blank)
    {
        var report = RemoteExecutorReport.Build(["a", "b", "", blank!],
        [
            new(At, blank!, "skip-1", "lane", "shadow-skipped-no-executor", null),
            new(At.AddMinutes(2), blank!, "skip-2", "lane", "shadow-skipped-no-executor", null),
            new(At, blank!, "other", "lane", "accepted", null, 100),
            new(At, "a", "real", "lane", "accepted", null, 2)
        ], [new(At, blank!, true, 0, false, null, null, [], "")]);

        Assert.Equal(new[] { "a", "b" }, report.Executors.Select(row => row.ExecutorId));
        Assert.All(report.Executors, row => Assert.False(string.IsNullOrWhiteSpace(row.ExecutorId)));
        Assert.Equal(2, report.NoExecutorFreeCount);
        Assert.Equal(At.AddMinutes(2), report.NoExecutorFreeLastAt);
        Assert.Equal(1, report.Executors[0].Attempts);
        Assert.Equal(2d, Assert.Single(report.Executors[0].Lanes).MedianSeconds);
        Assert.Equal(0, report.UnreadableOutcomeLines);
        Assert.Equal(0, report.UnreadableProbeLines);
    }

    [Fact]
    public void SkipsUseInclusiveWindowAndOnlyBlankIds()
    {
        RemoteExecutorOutcomeRow[] outcomes =
        [
            new(At, "", "1", "lane", "shadow-skipped-no-executor", null),
            new(At.AddMinutes(2), " ", "2", "lane", "shadow-skipped-no-executor", null),
            new(At.AddMinutes(3), "a", "3", "lane", "shadow-skipped-no-executor", null)
        ];
        var window = RemoteExecutorReport.Build(["a", "b"], outcomes, [], since: At.AddMinutes(1));
        Assert.Equal(1, window.NoExecutorFreeCount);
        Assert.Equal(At.AddMinutes(2), window.NoExecutorFreeLastAt);
        Assert.Equal(1, RemoteExecutorReport.Build([], outcomes, [], since: At.AddMinutes(2)).NoExecutorFreeCount);
        var empty = RemoteExecutorReport.Build([], outcomes, [], since: At.AddMinutes(3));
        Assert.Equal(0, empty.NoExecutorFreeCount);
        Assert.Null(empty.NoExecutorFreeLastAt);
        var none = RemoteExecutorReport.Build([], [], []);
        Assert.Equal(0, none.NoExecutorFreeCount);
        Assert.Null(none.NoExecutorFreeLastAt);
    }

    [Fact]
    public void SkipSummaryDoesNotChangeJsonPayload()
    {
        var report = RemoteExecutorReport.Build(["a"],
            [new(At, "", "skip", "lane", "shadow-skipped-no-executor", null)], []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "executors", "unreadableOutcomeLines", "unreadableProbeLines" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
    }
}
