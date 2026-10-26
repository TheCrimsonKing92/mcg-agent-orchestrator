using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: no live conductor, native sound or shared state is accessed.
public sealed class OwnerConsoleNotificationDetectorConductorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StopRingsOnceAndStoppedBaselineIsSilent()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        Assert.Empty(new OwnerConsoleNotificationDetector(new()).Detect(null, stopped, Now, OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void TickCrossingRingsOnceAndNewerTickRearms()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var model = Model(true, Now.AddMinutes(-14));
        detector.Detect(null, model, Now, OwnerConsoleBellTiers.All);
        Assert.Empty(detector.Detect(model, model, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(model, model, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(model, model, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
        var renewed = Model(true, Now.AddMinutes(2));
        Assert.Empty(detector.Detect(model, renewed, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(renewed, renewed, Now.AddMinutes(18), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void MissingOlderOrInitiallyStaleTicksDoNotReplayAlarm()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var stale = Model(true, Now.AddMinutes(-16));
        Assert.Empty(detector.Detect(null, stale, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stale, stale, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        var missing = Model(true);
        Assert.Empty(detector.Detect(stale, missing, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(missing, stale, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
        var older = Model(true, Now.AddMinutes(-20));
        Assert.Empty(detector.Detect(stale, older, Now.AddMinutes(4), OwnerConsoleBellTiers.All));
        var stopped = Model(false, Now.AddMinutes(-20));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(older, stopped, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void UnknownTickCanBecomeFreshAndCrossWhileOldEventAgeIsIgnored()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var unknown = Model(true);
        detector.Detect(null, unknown, Now, OwnerConsoleBellTiers.All);
        var fresh = Model(true, Now) with { Status = Model(true, Now).Status with { LastEventAge = TimeSpan.FromHours(2) } };
        Assert.Empty(detector.Detect(unknown, fresh, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(fresh, fresh, Now.AddMinutes(16), OwnerConsoleBellTiers.All));
        var missing = Model(true);
        Assert.Empty(detector.Detect(fresh, missing, Now.AddMinutes(17), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(missing, Model(false), Now.AddMinutes(18), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void RestartGetsAFullFreshWindowAndCustomThresholdApplies()
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var stopped = Model(false, Now.AddHours(-1));
        var running = Model(true, Now.AddHours(-1));
        detector.Detect(null, stopped, Now, OwnerConsoleBellTiers.All);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(stopped, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, running, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm], detector.Detect(running, running, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public async Task BoardRefreshCarriesTickAndReadFailureGivesNull()
    {
        var harness = new OwnerConsoleHarness();
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock, latestTick: () => Now);
        Assert.Equal(Now, (await builder.BuildBoardAsync()).Status.LatestTickAt);
        Assert.Null((await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock).BuildBoardAsync()).Status.LatestTickAt);
        Assert.Null((await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock, latestTick: () => throw new IOException("unavailable"))
            .BuildBoardAsync()).Status.LatestTickAt);
    }

    private static OwnerConsoleViewModel Model(bool running, DateTimeOffset? tick = null) =>
        new(new(running, 0, 0, 0, null, 0, LatestTickAt: tick), [], [], []);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
