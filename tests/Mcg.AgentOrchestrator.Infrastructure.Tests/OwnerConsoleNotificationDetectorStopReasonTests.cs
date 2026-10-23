using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: events, models and time are in memory; no conductor, files or sound are used.
public sealed class OwnerConsoleNotificationDetectorStopReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlannedHandoffStopDoesNotRingAlarm()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false, "self-relaunch-handoff", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm],
            detector.Detect(stopped, running, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        // A successful restart must discard the deferred stop alarm.
        Assert.Empty(detector.Detect(running, running, Now.AddHours(1), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("max-duration")]
    public void UnexplainedStopStillRingsAlarm(string? reason)
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var planned = reason is null ? null : OwnerConsoleStopClassifier.Classify([Stop(reason)]);
        var running = Model(true);
        var stopped = Model(false, planned?.Reason, planned?.At);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void FailedHandoffAfterPlannedStopRingsAlarmOnce()
    {
        var detector = new OwnerConsoleNotificationDetector(new());
        var running = Model(true);
        var stopped = Model(false, "self-relaunch-handoff", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        var planned = OwnerConsoleStopClassifier.Classify([Stop("self-relaunch-handoff"),
            new(Now.AddMinutes(1), "loop-handoff", null, "LOOP_HANDOFF_FAILED reason=activation")]);
        var failed = Model(false, planned?.Reason, planned?.At);
        Assert.Null(failed.Status.PlannedStopReason);
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(stopped, failed, Now.AddMinutes(1), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(failed, failed, Now.AddMinutes(2), OwnerConsoleBellTiers.All));
    }

    [Fact]
    public void HandoffThatNeverReturnsRingsAlarmAfterThreshold()
    {
        foreach (var reason in new[] { "self-relaunch-handoff", "stop-file" })
        {
            var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
            var running = Model(true);
            var stopped = Model(false, reason, Now);
            Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
            Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
            Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(5), OwnerConsoleBellTiers.All));
            var afterThreshold = detector.Detect(stopped, stopped, Now.AddMinutes(6), OwnerConsoleBellTiers.All);
            if (reason == "self-relaunch-handoff") Assert.Equal([OwnerConsoleNotificationTier.Alarm], afterThreshold);
            else Assert.Empty(afterThreshold);
            Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(7), OwnerConsoleBellTiers.All));
        }
    }

    [Theory]
    [InlineData("self-relaunch-handoff", "self-relaunch-handoff")]
    [InlineData("stop-file", "stop-file")]
    [InlineData("stop-after-operator-intent", "stop-after-operator-intent")]
    [InlineData("stop-while-idle", "stop-while-idle")]
    [InlineData("stop-file-during-sleep", "stop-file-during-sleep")]
    [InlineData("handoff-then-start", null)]
    [InlineData("handoff-then-start-reversed", null)]
    [InlineData("start-then-handoff-reversed", "self-relaunch-handoff")]
    [InlineData("handoff-then-failed", null)]
    [InlineData("handoff-then-failed-both", null)]
    [InlineData("failure-before-stop", "self-relaunch-handoff")]
    [InlineData("failure-tied-after-stop", null)]
    [InlineData("failure-tied-before-stop", "self-relaunch-handoff")]
    [InlineData("nonfailure-handoff", "self-relaunch-handoff")]
    [InlineData("failure-head-substring", "self-relaunch-handoff")]
    [InlineData("failure-wrong-kind", "self-relaunch-handoff")]
    [InlineData("planned-then-unplanned", null)]
    [InlineData("max-duration", null)]
    [InlineData("max-iter", null)]
    [InlineData("no-progress-no-watch", null)]
    [InlineData("loop-return", null)]
    [InlineData("Stop-File", null)]
    [InlineData("stop-file-x", null)]
    [InlineData("missing-reason", null)]
    [InlineData("empty", null)]
    public void ClassifierReadsTheNewestStopOrStart(string scenario, string? expectedReason)
    {
        var handoff = Stop("self-relaunch-handoff");
        var start = new OwnerConductEvent(Now.AddMinutes(1), "loop-start", null, "LOOP_START");
        var failure = new OwnerConductEvent(Now.AddMinutes(1), "loop-handoff", null, "LOOP_HANDOFF_FAILED reason=activation");
        OwnerConductEvent[] events = scenario switch
        {
            "handoff-then-start" => [handoff, start],
            "handoff-then-start-reversed" => [start, handoff],
            "start-then-handoff-reversed" => [handoff, start with { Timestamp = Now.AddMinutes(-1) }],
            "handoff-then-failed" => [handoff, failure],
            "handoff-then-failed-both" => [handoff, failure with { Detail = "ACTIVATION_FAILED_BOTH reason=activation" }],
            "failure-before-stop" => [failure with { Timestamp = Now.AddMinutes(-1) }, handoff],
            "failure-tied-after-stop" => [handoff, failure with { Timestamp = Now }],
            "failure-tied-before-stop" => [failure with { Timestamp = Now }, handoff],
            "nonfailure-handoff" => [handoff, failure with { Detail = "ACTIVATION_REVERTED reason=activation" }],
            "failure-head-substring" => [handoff, failure with { Detail = "LOOP_HANDOFF_FAILEDX reason=activation" }],
            "failure-wrong-kind" => [handoff, failure with { EventKind = "other" }],
            "planned-then-unplanned" => [handoff, Stop("max-duration") with { Timestamp = Now.AddMinutes(1) }],
            "missing-reason" => [handoff with { Detail = "LOOP_STOP tick=1 rechecks=0" }],
            "empty" => [],
            _ => [Stop(scenario)]
        };

        var planned = OwnerConsoleStopClassifier.Classify(events);
        if (expectedReason is null) Assert.Null(planned);
        else
        {
            Assert.True(planned.HasValue);
            Assert.Equal(expectedReason, planned.Value.Reason);
            Assert.Equal(Now, planned.Value.At);
        }
    }

    [Fact]
    public async Task BuilderCarriesPlannedStopFromRecentEvents()
    {
        var harness = new OwnerConsoleHarness();
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock);
        var at = harness.Clock.GetUtcNow();
        var model = await builder.BuildAsync(new(at, at,
            [new(at.AddMinutes(-1), "loop-start", null, "LOOP_START"),
                new(at, "loop-stop", null, "LOOP_STOP tick=4 rechecks=0 reason=self-relaunch-handoff")], 0));
        Assert.Equal("self-relaunch-handoff", model.Status.PlannedStopReason);
        Assert.Equal(at, model.Status.PlannedStopAt);

        var empty = await builder.BuildAsync(new(at, null, [], 0));
        Assert.Null(empty.Status.PlannedStopReason);
        Assert.Null(empty.Status.PlannedStopAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("self-relaunch-handoff")]
    [InlineData("stop-file")]
    public void AlreadyStoppedBaselineDoesNotArmAnAlarm(string? reason)
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var stopped = Model(false, reason, Now);
        Assert.Empty(detector.Detect(null, stopped, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, stopped, Now.AddMinutes(6), OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(stopped, Model(false), Now.AddMinutes(7), OwnerConsoleBellTiers.All));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutedDeferredAlarmIsConsumedAndRestartRearms(bool timedOut)
    {
        var detector = new OwnerConsoleNotificationDetector(new() { StaleTickThreshold = TimeSpan.FromMinutes(5) });
        var running = Model(true);
        var stopped = Model(false, "self-relaunch-handoff", Now);
        Assert.Empty(detector.Detect(null, running, Now, OwnerConsoleBellTiers.All));
        Assert.Empty(detector.Detect(running, stopped, Now, OwnerConsoleBellTiers.All));
        var failed = timedOut ? stopped : Model(false);
        var muted = OwnerConsoleBellTiers.All with { Alarm = false };
        Assert.Empty(detector.Detect(stopped, failed, Now.AddMinutes(6), muted));
        Assert.Empty(detector.Detect(failed, failed, Now.AddMinutes(7), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Calm],
            detector.Detect(failed, running, Now.AddMinutes(8), OwnerConsoleBellTiers.All));
        var nextStop = Model(false, "self-relaunch-handoff", Now.AddMinutes(9));
        Assert.Empty(detector.Detect(running, nextStop, Now.AddMinutes(9), OwnerConsoleBellTiers.All));
        Assert.Equal([OwnerConsoleNotificationTier.Alarm],
            detector.Detect(nextStop, nextStop, Now.AddMinutes(15), OwnerConsoleBellTiers.All));
    }

    private static OwnerConductEvent Stop(string reason) =>
        new(Now, "loop-stop", null, $"LOOP_STOP tick=1 rechecks=0 reason={reason}");

    private static OwnerConsoleViewModel Model(bool running, string? reason = null, DateTimeOffset? at = null) =>
        new(new(running, 0, 0, 0, null, 0, PlannedStopReason: reason, PlannedStopAt: at), [], [], []);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }
}
