using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Timeout = System.Threading.Timeout;

// Parallel-safe: each test owns its clock, strip and uninitialized application.
public sealed class OwnerConsoleNoticeStripTests
{
    [Fact]
    public void RepeatedRefreshFailureCoalescesWithoutRemovingOtherSources()
    {
        var clock = new OwnerConsoleTestClock();
        var strip = new OwnerConsoleNoticeStrip(clock);
        strip.Raise("unavailable", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        strip.Raise("queued", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        strip.Raise("unavailable", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        clock.Now += TimeSpan.FromSeconds(1);
        for (var i = 0; i < OwnerConsoleNoticeStrip.MaxEntries * 2; i++)
            strip.Raise("unavailable", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);

        var entries = strip.Visible;
        Assert.Equal(3, entries.Count);
        Assert.Equal(clock.GetLocalNow(), entries[0].RaisedAt);
        Assert.Equal(OwnerConsoleNoticeSource.Refresh, entries[0].Source);
        Assert.Equal("queued", entries[1].Text);
        Assert.Equal(OwnerConsoleNoticeSource.Action, entries[2].Source);
        strip.RefreshSucceeded();
        Assert.Equal(["queued", "unavailable"], strip.Visible.Select(entry => entry.Text));
    }

    [Fact]
    public void DistinctFailuresStayBoundedAndRetainNewestEntries()
    {
        var strip = new OwnerConsoleNoticeStrip(new OwnerConsoleTestClock());
        for (var i = 0; i < OwnerConsoleNoticeStrip.MaxEntries * 2; i++)
            strip.Raise($"failure {i}", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);

        Assert.Equal(Enumerable.Range(OwnerConsoleNoticeStrip.MaxEntries, OwnerConsoleNoticeStrip.MaxEntries)
            .Reverse().Select(i => $"failure {i}"), strip.Visible.Select(entry => entry.Text));
    }

    [Fact]
    public async Task EarlyExpiryTimerRearmsAndClearsRenderedNoticeWithoutRefresh()
    {
        var clock = new OwnerConsoleTestClock();
        var scheduled = new List<TimeSpan>();
        clock.TimerChanged = scheduled.Add;
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Ship?"));
        using IApplication app = Application.Create();
        using var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers,
            new OnHelp(() => { }), harness.State, harness.Tail, harness.Conductor,
            harness.DigestReport, harness.Digest, clock);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, clock: clock);
        Assert.Empty(scheduled); // Construction must not create even an inactive timer.
        view.Render(await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), clock).BuildAsync(new(clock.Now, null, [], 0)));
        var history = Assert.Single(view.ActivityLines);
        view.ShowNotice("refresh failed", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        view.RefreshStatus();
        Assert.Contains("refresh failed", view.NoticeText);
        Assert.Empty(scheduled); // Failures wait for recovery, so no expiry timer is needed.
        view.NoticeStrip.RefreshSucceeded();
        view.RefreshStatus();
        Assert.Empty(view.NoticeText);
        Assert.Empty(scheduled);
        view.ShowNotice("queued", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        Assert.Equal("00:05:00 queued", view.NoticeText);
        Assert.Equal([history], view.ActivityLines);
        Assert.Equal(history, Assert.Single(view.ActivityPane.Source!.ToList().Cast<string>()));
        Assert.Equal(OwnerConsoleNoticeStrip.SuccessLifetime, scheduled[^1]);

        var remainder = TimeSpan.FromMilliseconds(1);
        clock.Now += OwnerConsoleNoticeStrip.SuccessLifetime - remainder;
        clock.Advance(OwnerConsoleNoticeStrip.SuccessLifetime); // Timer fires before the wall clock reaches expiry.
        Assert.Equal("00:05:00 queued", view.NoticeText);
        Assert.Equal(remainder, scheduled[^1]);

        clock.Now += remainder;
        clock.Advance(remainder);
        Assert.Empty(view.NoticeText); // Read rendered UI before Visible can prune the model.
        Assert.Empty(view.NoticeStrip.Visible);
        Assert.Equal(Timeout.InfiniteTimeSpan, scheduled[^1]);

        view.ShowNotice("first", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        clock.Now += TimeSpan.FromSeconds(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        view.ShowNotice("second", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        Assert.Equal(OwnerConsoleNoticeStrip.SuccessLifetime - TimeSpan.FromSeconds(1), scheduled[^1]);
        clock.Now += OwnerConsoleNoticeStrip.SuccessLifetime - TimeSpan.FromSeconds(1);
        clock.Advance(OwnerConsoleNoticeStrip.SuccessLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal("00:05:06 second", view.NoticeText);
        clock.Now += TimeSpan.FromSeconds(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(view.NoticeText);
        Assert.Equal([history], view.ActivityLines);
    }

    [Fact]
    public void SuccessExpiresWhileFailuresWaitForTheirOwnRecovery()
    {
        var clock = new OwnerConsoleTestClock();
        var strip = new OwnerConsoleNoticeStrip(clock);
        strip.Raise("queued", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        strip.Raise("refresh failed", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        strip.Raise("command failed", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        Assert.Equal(["command failed", "refresh failed", "queued"], strip.Visible.Select(entry => entry.Text));

        clock.Now += OwnerConsoleNoticeStrip.SuccessLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(["command failed", "refresh failed"], strip.Visible.Select(entry => entry.Text));
        strip.RefreshSucceeded();
        Assert.Equal("command failed", Assert.Single(strip.Visible).Text);
        strip.ActionStarted();
        Assert.Empty(strip.Visible);
    }

    [Fact]
    public void TimestampUsesRaiseTimeAndTheInjectedLocalZone()
    {
        var clock = new OwnerConsoleTestClock
        {
            Zone = TimeZoneInfo.CreateCustomTimeZone("Notice zone", TimeSpan.FromHours(2), "Notice zone", "Notice zone")
        };
        var strip = new OwnerConsoleNoticeStrip(clock);
        strip.Raise("queued", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        clock.Now = clock.Now.AddSeconds(1);

        var entry = Assert.Single(strip.Visible);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 5, 0, TimeSpan.FromHours(2)), entry.RaisedAt);
        Assert.Equal(OwnerConsoleNoticeSeverity.Success, entry.Severity);
        Assert.Equal("02:05:00 queued", strip.Format(80));
        Assert.Equal("02:05:00 …", strip.Format(10));
    }

    [Fact]
    public async Task ViewRecoveryClearsRefreshAtRenderAndActionAtNextActionStart()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = new OwnerConsoleHarness();
        using IApplication app = Application.Create();
        OwnerConsoleFullScreenView? view = null;
        var dialogs = new OnHelp(() =>
        {
            // Assert at action entry, before a refresh or successful completion could clear it.
            Assert.Empty(view!.NoticeStrip.Visible);
            Assert.Empty(view.NoticeText);
        });
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock);
        using var ownedView = view = new(app, controller, () => Task.CompletedTask, clock: clock);
        var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), clock).BuildAsync(new(clock.Now, null, [], 0));
        view.Render(model);
        view.ShowNotice("refresh failed", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        clock.Now += OwnerConsoleNoticeStrip.SuccessLifetime + TimeSpan.FromSeconds(1);
        view.RefreshStatus();
        Assert.Contains("refresh failed", view.NoticeText);
        view.Render(model);
        Assert.Empty(view.NoticeStrip.Visible);
        Assert.Empty(view.NoticeText);

        view.ShowNotice("command failed", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        view.Render(model);
        Assert.Contains("command failed", view.NoticeText);
        await view.HandleKeyAsync(new Key('?'));
        Assert.Equal(1, dialogs.Calls);
    }

    private sealed class OnHelp(Action opened) : IOwnerConsoleDialogs
    {
        internal int Calls;
        public Task ShowTextAsync(string title, string text)
        { Calls++; opened(); return Task.CompletedTask; }
        public Task<string?> PromptTextAsync(string title, string text) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string text) => throw new NotSupportedException();
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
