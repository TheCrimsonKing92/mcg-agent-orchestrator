using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: fixed models and an uninitialized instance application.
public sealed class OwnerConsoleGateViewTests
{
    [Fact]
    public void FixedModelRendersExactProgressAllMarkersAndQuietBoundary()
    {
        var gate = Model();
        var lines = OwnerConsoleGateFormatter.Lines(gate);
        Assert.Equal(" x 1 failed   > 1 running   + 1 passed   = 1 cached   . 1 queued", lines[3]);
        Assert.Equal(["FAILED", " x Failed        20s / 50s   local         failed", "RUNNING (longest pole first)",
            " > Running       80s / 200s  local slot 2  [####------] quiet 7m", "QUEUED",
            " . Queued          - / 100s  local", "PASSED  2 lanes (Enter to expand)",
            "DONE  1 without a recorded outcome", "WHERE   local 2 [----------]   local [##########]   remote [##########]"], lines.Skip(4));
        Assert.DoesNotContain("quiet", OwnerConsoleGateFormatter.LaneLine(gate.Lanes[0] with { QuietSeconds = 300 }));
        Assert.DoesNotContain("quiet", OwnerConsoleGateFormatter.LaneLine(gate.Lanes[3] with { QuietSeconds = 900 }));
        Assert.Equal("gate: cohort 776a+eade 4/6 FAILING", OwnerConsoleGateFormatter.StatusSegment(gate));
        Assert.Equal("gate: idle 12m", OwnerConsoleGateFormatter.StatusSegment(OwnerConsoleGateProgress.Idle(TimeSpan.FromMinutes(12))));
        Assert.Equal(["gate: idle"], OwnerConsoleGateFormatter.Lines(OwnerConsoleGateProgress.Idle()));
    }

    [Theory]
    [InlineData(0, "~0m")]
    [InlineData(1, "~<1m")]
    [InlineData(59, "~<1m")]
    [InlineData(60, "~1m")]
    [InlineData(61, "~2m")]
    public void EstimateRoundingIsPinned(int seconds, string expected) =>
        Assert.Equal(expected, OwnerConsoleGateFormatter.Remaining(seconds));

    [Fact]
    public async Task GOpensLiveViewRefreshesAndEscapeRestoresBoardFocus()
    {
        var harness = new OwnerConsoleHarness();
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(), harness.State,
            harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var model = new OwnerConsoleViewModel(new(true, 0, 0, 0, null, 0), [], [], [], Gate: Model());
        view.Render(model);
        view.FocusDecisions();
        await view.HandleKeyAsync(Key.Tab);

        await view.HandleKeyAsync(new Key('g'));

        Assert.True(view.GateView.IsOpen);
        Assert.Equal(" x 1 failed   > 1 running   + 1 passed   = 1 cached   . 1 queued", view.GateView.Lines[3]);
        Assert.Contains("gate: cohort 776a+eade 4/6 FAILING", view.StatusText);
        view.Render(model with { Gate = OwnerConsoleGateProgress.Idle(TimeSpan.FromMinutes(12)) });
        Assert.Equal(["gate: idle 12m"], view.GateView.Lines);
        Assert.Contains("gate: idle 12m", view.StatusText);
        await view.HandleKeyAsync(Key.Esc);
        Assert.False(view.GateView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
    }

    private static OwnerConsoleGateProgress Model() => new(true, "cohort",
        [new("776a132e", "First"), new("eade84cb", "Second")], DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(24),
        [
            new("Running", OwnerConsoleGateLaneState.Running, "unknown", "local slot 2", TimeSpan.FromSeconds(80), 200, false, 420),
            new("Passed", OwnerConsoleGateLaneState.Done, "passed", "local", TimeSpan.FromSeconds(100), 100, false),
            new("Failed", OwnerConsoleGateLaneState.Done, "failed", "local", TimeSpan.FromSeconds(20), 50, false),
            new("Queued", OwnerConsoleGateLaneState.Queued, "unknown", "local", TimeSpan.Zero, 100, true),
            new("Cached", OwnerConsoleGateLaneState.Cached, "passed", "local", TimeSpan.Zero, 200, false),
            new("Legacy", OwnerConsoleGateLaneState.Done, "unknown", "remote", TimeSpan.FromSeconds(40), 100, false)
        ], .62, 540, 4, 6);

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
