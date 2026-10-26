using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: models, detector state and injected times are local to each test.
public sealed class OwnerConsoleNotificationDetectorLandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LandingsCoalesceAndResumeAfterWindow()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var previous = Model(3);
        Assert.Empty(detector.Detect(null, previous, Now, OwnerConsoleBellTiers.All));
        var fourth = Model(4);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(previous, fourth, Now, OwnerConsoleBellTiers.All));
        var fifth = Model(5);
        Assert.Empty(detector.Detect(fourth, fifth, Now.AddSeconds(20), OwnerConsoleBellTiers.All));
        var sixth = Model(6);
        Assert.Empty(detector.Detect(fifth, sixth, Now.AddSeconds(40), OwnerConsoleBellTiers.All));
        var seventh = Model(7);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(sixth, seventh, Now.AddSeconds(61), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(seventh, Model(0), Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void StartSharesTheLandingCoalesceWindow()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var stopped = Model(3);
        detector.Detect(null, stopped, Now, OwnerConsoleBellTiers.All);
        var running = Model(3, true);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(stopped, running, Now, OwnerConsoleBellTiers.All));
        var landed = Model(4, true);
        Assert.Empty(detector.Detect(running, landed, Now.AddSeconds(30), OwnerConsoleBellTiers.All));
        detector.Detect(landed, stopped, Now.AddSeconds(40), OwnerConsoleBellTiers.All);
        Assert.DoesNotContain(OwnerConsoleNotificationTier.Calm,
            detector.Detect(stopped, running, Now.AddSeconds(50), OwnerConsoleBellTiers.All));
        detector.Detect(running, stopped, Now.AddSeconds(60), OwnerConsoleBellTiers.All);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(stopped, running, Now.AddSeconds(61), OwnerConsoleBellTiers.All));
    }

    private static OwnerConsoleViewModel Model(int landed, bool running = false) =>
        new(new(running, 0, 0, 0, null, landed), [], [], []);
}
