using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: each test owns its clock, strip and uninitialized application.
public sealed class OwnerConsoleNoticeStripTests
{
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
