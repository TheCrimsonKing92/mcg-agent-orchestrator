using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each detector and all inputs belong to a single test.
public sealed class OwnerConsoleNotificationDetectorNegativeControlTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NullLoadingErrorAndUnchangedModelsAreBaselineOnly()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var loading = Model(0) with { DecisionsState = new(Loading: true) };
        Assert.Empty(detector.Detect(null, loading, Now, OwnerConsoleBellTiers.All));
        var full = Model(9);
        Assert.Empty(detector.Detect(loading, full, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(full, full, Now.AddSeconds(2), OwnerConsoleBellTiers.All));
        var activityLoading = full with { ActivityState = new(Loading: true) };
        Assert.Empty(detector.Detect(full, activityLoading, Now.AddSeconds(3), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(activityLoading, Model(12), Now.AddSeconds(4), OwnerConsoleBellTiers.All));
        var error = Model(12) with { ActivityState = new(Error: "unavailable") };
        Assert.Empty(detector.Detect(Model(12), error, Now.AddSeconds(5), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(error, Model(20), Now.AddSeconds(6), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void MutedEventsAreConsumedAndDoNotStartCalmWindow()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var baseline = Model(0);
        detector.Detect(null, baseline, Now, OwnerConsoleBellTiers.All);
        var landed = Model(1);
        Assert.Empty(detector.Detect(baseline, landed, Now, OwnerConsoleBellTiers.All.With(OwnerConsoleNotificationTier.Calm, false)));
        Assert.Empty(detector.Detect(landed, landed, Now.AddSeconds(1), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm], detector.Detect(landed, Model(2), Now.AddSeconds(2), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MutedTimeCrossingsAreNotReplayed(int tierValue)
    {
        var tier = (OwnerConsoleNotificationTier)tierValue;
        var detector = new OwnerConsoleNotificationDetector(new());
        var decision = new OwnerConsoleDecision("q", 1, "goal", "goal", OwnerQuestionKind.HumanInput,
            "Answer?", "Answer?", null, null, null, Now.AddMinutes(-29));
        var model = new OwnerConsoleViewModel(new(true, 0, 1, 0, null, 0, LatestTickAt: Now.AddMinutes(-14)), [decision], [], []);
        detector.Detect(null, model, Now, OwnerConsoleBellTiers.All);
        var result = detector.Detect(model, model, Now.AddMinutes(2), OwnerConsoleBellTiers.All.With(tier, false));
        Assert.DoesNotContain(tier, result);
        Assert.Empty(detector.Detect(model, model, Now.AddMinutes(3), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void SimultaneousTiersReturnInAscendingSeverity()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var decision = new OwnerConsoleDecision("q", 1, "goal", "goal", OwnerQuestionKind.HumanInput,
            "Answer?", "Answer?", null, null, null, Now.AddMinutes(-29));
        var baseline = Model(0) with { Decisions = [decision], Status = Model(0).Status with { ConductorRunning = true } };
        detector.Detect(null, baseline, Now, OwnerConsoleBellTiers.All);
        var current = Model(1) with { Decisions = [decision] };
        Assert.Equal([OwnerConsoleNotificationTier.Calm, OwnerConsoleNotificationTier.Attention, OwnerConsoleNotificationTier.Alarm],
            detector.Detect(baseline, current, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    private static OwnerConsoleViewModel Model(int landed) => new(new(false, 0, 0, 0, null, landed), [], [], []);
}
