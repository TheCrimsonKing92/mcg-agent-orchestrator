using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

// Parallel-safe: pure event inputs, instance clocks and an uninitialized headless UI application.
public sealed class OwnerConsoleProgramHealthNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 5, 20, 0, TimeSpan.Zero);
    private static OwnerConductEvent Alert(string diagnosis = "Nothing landed for 1 h 35 min; ...", int minutes = 0) =>
        new(Now.AddMinutes(minutes), "program-health", null, "PROGRAM_HEALTH_ALERT conditions=held-receipt diagnosis=" + diagnosis);

    [Fact]
    public void EmptyAndOtherKindsDoNotProduceNotice()
    {
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([]));
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([Alert() with { EventKind = "other" }]));
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([Alert() with { Detail = "PROGRAM_HEALTH_ALERT_EXTRA diagnosis=bad" }]));
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([Alert() with { Detail = " PROGRAM_HEALTH_ALERT diagnosis=bad" }]));
    }

    [Fact]
    public void LatestProgramHealthTransitionDeterminesNoticeRegardlessOfEnumerationOrder()
    {
        var first = Alert();
        var newer = Alert("new diagnosis", 1);
        var clear = newer with { Timestamp = Now.AddMinutes(2), Detail = "PROGRAM_HEALTH_CLEARED previous=x" };
        Assert.Equal("Program health: Nothing landed for 1 h 35 min; ...", OwnerConsoleProgramHealthNotice.Current([first]));
        Assert.Equal("Program health: new diagnosis", OwnerConsoleProgramHealthNotice.Current([newer, first]));
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([clear, first, newer]));
        Assert.Equal("Program health: new diagnosis", OwnerConsoleProgramHealthNotice.Current([newer, clear with { EventKind = "other" }]));
        Assert.Null(OwnerConsoleProgramHealthNotice.Current([first, clear with { Timestamp = Now }]));
        Assert.Equal("Program health: Nothing landed for 1 h 35 min; ...",
            OwnerConsoleProgramHealthNotice.Current([clear with { Timestamp = Now }, first]));
    }

    [Fact]
    public void NoticeSurvivesRefreshActionsAndTimeAndOnlyReplacesItsOwnSource()
    {
        var clock = new OwnerConsoleTestClock { Now = Now };
        var strip = new OwnerConsoleNoticeStrip(clock);
        strip.Raise("refresh failure", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Refresh);
        strip.Raise("action failure", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        strip.SetProgramHealth("health");
        var original = Assert.Single(strip.Visible, item => item.Source == OwnerConsoleNoticeSource.ProgramHealth);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, original.Severity);
        clock.Now = clock.Now.AddMinutes(10);
        strip.SetProgramHealth("health");
        Assert.Equal(original, Assert.Single(strip.Visible, item => item.Source == OwnerConsoleNoticeSource.ProgramHealth));
        Assert.Contains(strip.Visible, item => item.Text == "refresh failure");
        Assert.Contains(strip.Visible, item => item.Text == "action failure");
        strip.RefreshSucceeded();
        strip.ActionStarted();
        Assert.Equal(original, Assert.Single(strip.Visible));
        strip.Raise("other", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        strip.SetProgramHealth("replacement");
        Assert.Equal("replacement", Assert.Single(strip.Visible, item => item.Source == OwnerConsoleNoticeSource.ProgramHealth).Text);
        strip.SetProgramHealth(null);
        Assert.Equal("other", Assert.Single(strip.Visible).Text);
    }

    [Fact]
    public void ProgramHealthEntersRecentEventsWithoutAnActivitySentence()
    {
        var item = Alert();
        Assert.True(OwnerActivityNarrator.Maps(item));
        Assert.True(OwnerConsoleStartupActivity.IsOperatorEvent(item));
        Assert.Empty(OwnerActivityNarrator.Narrate([item], _ => "goal title"));
    }

    [Fact]
    public async Task HeadlessRenderKeepsProgramHealthFailure()
    {
        var harness = new OwnerConsoleHarness();
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var text = OwnerConsoleProgramHealthNotice.Current([Alert()]);
        view.NoticeStrip.SetProgramHealth(text);
        var (model, _) = await OwnerConsoleFullScreenHost.BuildInitialViewModelAsync(builder,
            Path.Combine(Path.GetTempPath(), "program-health-missing-" + Guid.NewGuid().ToString("N"), "events.jsonl"),
            harness.Clock.GetUtcNow(), Now, TestContext.Current.CancellationToken);
        view.Render(model);
        var notice = Assert.Single(view.NoticeStrip.Visible);
        Assert.Equal(text, notice.Text);
        Assert.Equal(OwnerConsoleNoticeSource.ProgramHealth, notice.Source);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, notice.Severity);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Must not prompt.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Must not prompt.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Must not open dialog.");
    }
}
