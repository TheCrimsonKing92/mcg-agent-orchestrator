using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: the presenter is stateless and all time inputs are fixed values.
public sealed class OwnerStatusPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
    private static readonly OwnerConsoleStatus Status = new(false, 3, 2, 0, TimeSpan.FromSeconds(42), 5);
    private readonly OwnerStatusPresenter _presenter = new();

    [Theory]
    [InlineData(false, "conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off")]
    [InlineData(true, "conductor: running | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off")]
    public void FormatsConductorState(bool running, string expected)
    {
        Assert.Equal(expected, Compose(Status with { ConductorRunning = running }));
    }

    [Fact]
    public void AppendsHiddenQuestions()
    {
        Assert.Equal("conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | hidden: 2",
            Compose(Status with { HiddenQuestions = 2 }));
    }

    [Theory]
    [InlineData(-1, "conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | refreshed 00:05:00")]
    [InlineData(0, "conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | refreshed 00:05:00")]
    [InlineData(1, "conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | stale 1m")]
    public void BecomesStaleOnlyAfterThreeRefreshIntervals(int ticksPastBoundary, string expected)
    {
        Assert.Equal(expected, Compose(refreshedAt: Now, now: Now.AddSeconds(30).AddTicks(ticksPastBoundary)));
    }

    [Fact]
    public void ConvertsRefreshTimeToSuppliedLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test+02", "Test+02");

        Assert.Equal("conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | refreshed 02:05:00",
            Compose(refreshedAt: Now, localZone: zone));
    }

    [Fact]
    public void AppendsFailedToday()
    {
        Assert.Equal("conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | failed today: 1",
            Compose(Status with { FailedToday = 1 }));
    }

    [Fact]
    public void AppendsWorkingLabelsInSuppliedOrder()
    {
        Assert.Equal("conductor: stopped | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: off | working: refresh after conductor event, command",
            Compose(working: ["refresh after conductor event", "command"]));
    }

    [Fact]
    public void PreservesGateAndSuffixOrder()
    {
        Assert.Equal("conductor: stopped | gate: idle 2m | active: 3 | decisions: 2 | last event: 42s | landed today: 5 | bell: on | hidden: 2 | stale 6m | failed today: 1 | working: refresh, command",
            _presenter.Compose(Status with { HiddenQuestions = 2, FailedToday = 1 },
                OwnerConsoleGateProgress.Idle(TimeSpan.FromMinutes(2)), "on", Now, Now.AddMinutes(6),
                TimeZoneInfo.Utc, TimeSpan.FromSeconds(10), ["refresh", "command"]));
    }

    [Theory]
    [InlineData(null, "conductor: stopped | active: 3 | decisions: 2 | last event: unknown | landed today: 5 | bell: off")]
    [InlineData(59.6, "conductor: stopped | active: 3 | decisions: 2 | last event: 60s | landed today: 5 | bell: off")]
    [InlineData(60.0, "conductor: stopped | active: 3 | decisions: 2 | last event: 1m | landed today: 5 | bell: off")]
    [InlineData(3600.0, "conductor: stopped | active: 3 | decisions: 2 | last event: 1h | landed today: 5 | bell: off")]
    public void PreservesEventAgeFormatting(double? seconds, string expected)
    {
        var status = Status with { LastEventAge = seconds is { } value ? TimeSpan.FromSeconds(value) : null };

        Assert.Equal(expected, Compose(status));
    }

    private string Compose(OwnerConsoleStatus? status = null, DateTimeOffset? refreshedAt = null,
        DateTimeOffset? now = null, TimeZoneInfo? localZone = null, IReadOnlyCollection<string>? working = null) =>
        _presenter.Compose(status ?? Status, null, "off", refreshedAt, now ?? Now,
            localZone ?? TimeZoneInfo.Utc, TimeSpan.FromSeconds(10), working ?? []);
}
