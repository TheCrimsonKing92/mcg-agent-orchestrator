// Parallel-safe: fixed clocks, instance-only models and uninitialized applications.
using System.Collections.Concurrent;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using EpicFixture = OwnerConsoleEpicViewTests.Fixture;

public sealed class OwnerConsoleDialogOverflowScenarioTests
{
    [Fact]
    public async Task Gate_PagingAndLifecycle_TrackLoadedRowsAndRestoreFocus()
    {
        using var fixture = new GateFixture();
        var view = fixture.View;
        var gate = Gate(20);
        view.Render(Model(gate));
        view.FocusDecisions();
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(new Key('g'));
        await view.HandleKeyAsync(Key.Enter);
        view.Window.Layout(new Size(100, 20));
        var dialog = view.GateView;
        var lines = dialog.Lines.ToArray();
        var n = lines.Length;
        var h = dialog.BodyPane.Viewport.Height;
        Assert.True(h > 0 && n > h);
        await Scroll(Key.Home);
        Assert.Equal($"GATE · {n - h} below", dialog.Title);
        await Scroll(Key.End);
        Assert.Equal($"GATE · {n - h} above", dialog.Title);
        await Scroll(Key.Home);
        await Scroll(Key.PageDown);
        var overflow = OwnerConsoleOverflow.Of(n, dialog.BodyPane.Viewport.Y, h);
        Assert.True(overflow.Above > 0 && overflow.Below > 0);
        Assert.Equal(OwnerConsoleOverflow.Title("GATE", overflow, 96), dialog.Title);

        gate = gate with { Lanes = gate.Lanes.Select(lane => lane with { Elapsed = TimeSpan.FromSeconds(99) }).ToArray() };
        view.Render(Model(gate));
        Assert.True(dialog.Expanded);
        AssertGateTitle(dialog, 96);
        await view.HandleKeyAsync(Key.Esc);
        Assert.False(dialog.Expanded);
        AssertGateTitle(dialog, 96);
        await view.HandleKeyAsync(Key.Enter);
        view.Window.Layout(new Size(100, 60));
        Assert.Equal("GATE", dialog.Title);
        view.Window.Layout(new Size(14, 20));
        await view.HandleKeyAsync(Key.End);
        Assert.Equal(dialog.Lines.Count - dialog.BodyPane.Viewport.Height, dialog.BodyPane.Viewport.Y);
        Assert.Equal($"GATE ^{dialog.BodyPane.Viewport.Y}", dialog.Title);
        await view.HandleKeyAsync(Key.Esc);
        await view.HandleKeyAsync(Key.Esc);
        Assert.False(dialog.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
        Assert.True(view.BoardPane.HasFocus);

        async Task Scroll(Key key)
        {
            var previous = dialog.SelectedLine;
            await view.HandleKeyAsync(key);
            Assert.Equal(OwnerConsolePaneNavigator.Target(key, previous, n, h), dialog.SelectedLine);
            Assert.Equal(lines, dialog.Lines);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(20, false)]
    public async Task Gate_EmptyFittingOrUnlaidOut_KeepsBareTitle(int lanes, bool layout)
    {
        using var fixture = new GateFixture();
        var gate = lanes == 0 ? null : Gate(lanes);
        fixture.View.Render(Model(gate));
        if (layout) fixture.View.Window.Layout(new Size(100, 40));
        await AssertGateBaseline(fixture.View, gate);
    }

    [Fact]
    public async Task Gate_ReopenWithFittingContent_ClearsPreviousOverflow()
    {
        using var fixture = new GateFixture();
        var view = fixture.View;
        view.Render(Model(Gate(20)));
        await view.HandleKeyAsync(new Key('g'));
        await view.HandleKeyAsync(Key.Enter);
        view.Window.Layout(new Size(100, 20));
        Assert.NotEqual("GATE", view.GateView.Title);
        await view.HandleKeyAsync(Key.Esc);
        await view.HandleKeyAsync(Key.Esc);
        var gate = Gate(2);
        view.Render(Model(gate));
        view.Window.Layout(new Size(100, 40));
        await AssertGateBaseline(view, gate);
    }

    [Fact]
    public async Task Epics_DetailPagingResizeAndReopen_TrackLoadedRows()
    {
        using var fixture = new EpicFixture();
        var view = fixture.View;
        view.Window.Layout(new Size(46, 12));
        await view.HandleKeyAsync(new Key('e'));
        await view.HandleKeyAsync(Key.CursorDown);
        await view.HandleKeyAsync(Key.Enter);
        view.Window.Layout(new Size(46, 12));
        var dialog = view.EpicView;
        var l = dialog.Lines.Count;
        var h = dialog.BodyPane.Viewport.Height;
        Assert.True(h > 0 && l > h);
        Assert.Equal($"Epics · {l - h} below", dialog.Title);
        await Scroll(Key.PageDown);
        var overflow = OwnerConsoleOverflow.Of(l, dialog.BodyPane.Viewport.Y, h);
        Assert.True(overflow.Above > 0 && overflow.Below > 0);
        Assert.Equal(OwnerConsoleOverflow.Title("Epics", overflow, 42), dialog.Title);
        await ToBoundary(Key.PageDown);
        Assert.Equal($"Epics · {l - h} above", dialog.Title);
        await ToBoundary(Key.PageUp);
        Assert.Equal(0, dialog.BodyPane.SelectedItem);
        Assert.Equal($"Epics · {l - h} below", dialog.Title);
        view.Window.Layout(new Size(90, 12));
        AssertEpicTitle(dialog, 86);
        await view.HandleKeyAsync(Key.Esc);
        Assert.False(dialog.ShowingDetail);
        AssertEpicTitle(dialog, 86);
        await view.HandleKeyAsync(Key.Esc);
        view.Window.Layout(new Size(100, 40));
        using var baseline = new EpicFixture();
        baseline.View.Window.Layout(new Size(100, 40));
        await baseline.View.HandleKeyAsync(new Key('e'));
        await baseline.View.HandleKeyAsync(Key.CursorDown);
        await view.HandleKeyAsync(new Key('e'));
        AssertEpicBaseline(baseline.View.EpicView, dialog);

        async Task Scroll(Key key)
        {
            var header = dialog.HeaderLine;
            var hint = dialog.HintText;
            var lines = dialog.Lines.ToArray();
            await view.HandleKeyAsync(key);
            Assert.Equal(header, dialog.HeaderLine);
            Assert.Equal(hint, dialog.HintText);
            Assert.Equal(lines, dialog.Lines);
            AssertEpicTitle(dialog, 42);
        }

        async Task ToBoundary(Key key)
        {
            for (var step = 0; step <= l; step++)
            {
                var previous = dialog.BodyPane.SelectedItem;
                await Scroll(key);
                if (dialog.BodyPane.SelectedItem == previous) return;
            }
            Assert.Fail("Epic paging never reached its boundary.");
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Epics_ListUnavailableOrUnlaidOut_KeepsBaseline(bool wired, bool layout)
    {
        using var fixture = new EpicFixture(wired);
        using var baseline = new EpicFixture(wired);
        if (layout)
        {
            fixture.View.Window.Layout(new Size(100, 40));
            baseline.View.Window.Layout(new Size(100, 40));
        }
        await baseline.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(new Key('e'));
        AssertEpicBaseline(baseline.View.EpicView, fixture.View.EpicView);
        if (!wired || !layout) return;
        foreach (var key in new[] { Key.CursorUp, Key.CursorDown, new Key('w') })
        {
            await baseline.View.HandleKeyAsync(key);
            var expected = baseline.View.EpicView;
            var header = expected.HeaderLine;
            var hint = expected.HintText;
            var lines = expected.Lines.ToArray();
            await fixture.View.HandleKeyAsync(key);
            Assert.Equal("Epics", fixture.View.EpicView.Title);
            Assert.Equal(header, fixture.View.EpicView.HeaderLine);
            Assert.Equal(hint, fixture.View.EpicView.HintText);
            Assert.Equal(lines, fixture.View.EpicView.Lines);
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Epics_HeldFirstLoad_KeepsBareTitleAndLoadingBaseline()
    {
        using var fixture = new EpicFixture();
        using var baseline = new EpicFixture();
        fixture.View.Window.Layout(new Size(100, 40));
        baseline.View.Window.Layout(new Size(100, 40));
        var pending = fixture.Source.HoldNextLoad();
        var baselinePending = baseline.Source.HoldNextLoad();
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        var baselineOpening = baseline.View.HandleKeyAsync(new Key('e'));
        try
        {
            await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            await baseline.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            AssertEpicBaseline(baseline.View.EpicView, fixture.View.EpicView);
        }
        finally
        {
            fixture.Source.Release(pending, fixture.Time.GetUtcNow().AddHours(-24));
            baseline.Source.Release(baselinePending, baseline.Time.GetUtcNow().AddHours(-24));
            await Task.WhenAll(opening, baselineOpening).WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Decision_PagingAndObservedText_TrackTheExistingPage()
    {
        using IApplication app = Application.Create();
        var decision = Decision(string.Join('\n', Enumerable.Range(1, 40).Select(i => $"Question {i}")));
        using var detail = Detail(decision);
        var queue = new UiDispatchQueue();
        using var dialog = new OwnerConsoleDecisionDialog(detail, queue.Enqueue);
        queue.Drain();
        var expected = new OwnerConsoleTextPage(detail.State.Text, 60, 15);
        var l = dialog.Page.Lines.Count;
        const int h = 15;
        Assert.Equal(h, dialog.Page.Visible.Count);
        Assert.True(l > h);
        Assert.Equal($"Decision · {l - h} below", dialog.Title);
        AssertPage(expected, dialog.Page);
        dialog.BodyPane.SetFocus();
        Assert.True(dialog.BodyPane.HasFocus);
        await Scroll(Key.PageDown);
        var overflow = OwnerConsoleOverflow.Of(l, dialog.Page.Offset, h);
        Assert.True(overflow.Above > 0 && overflow.Below > 0);
        Assert.Equal(OwnerConsoleOverflow.Title("Decision", overflow, 0), dialog.Title);
        for (var step = 0; step <= l; step++)
        {
            var previous = dialog.Page.Offset;
            await Scroll(Key.PageDown);
            if (dialog.Page.Offset == previous) break;
            Assert.True(step < l, "Decision paging never reached its boundary.");
        }
        Assert.Equal($"Decision · {l - h} above", dialog.Title);
        detail.Observe(decision with { FullText = "First\nSecond\nThird" });
        queue.Drain();
        Assert.Equal("Decision", dialog.Title);
        AssertPage(new OwnerConsoleTextPage(detail.State.Text, 60, 15), dialog.Page);
        detail.Observe(decision);
        queue.Drain();
        Assert.Equal($"Decision · {l - h} below", dialog.Title);
        AssertPage(new OwnerConsoleTextPage(detail.State.Text, 60, 15), dialog.Page);

        async Task Scroll(Key key)
        {
            Assert.True(expected.HandleKey(key));
            await dialog.HandleKeyAsync(key);
            queue.Drain();
            AssertPage(expected, dialog.Page);
        }
    }

    [Fact]
    public async Task Decision_FittingQuestion_KeepsBareTitleAndPresentation()
    {
        using IApplication app = Application.Create();
        using var detail = Detail(Decision("Ship?"));
        using var baselineDetail = Detail(Decision("Ship?"));
        var queue = new UiDispatchQueue();
        using var baseline = new OwnerConsoleDecisionDialog(baselineDetail, queue.Enqueue);
        using var dialog = new OwnerConsoleDecisionDialog(detail, queue.Enqueue);
        var actions = baseline.Actions.ToArray();
        var banner = baseline.BannerText;
        var hint = baseline.HintText;
        var lines = baseline.Lines.ToArray();
        dialog.BodyPane.SetFocus();
        Assert.True(dialog.BodyPane.HasFocus);
        AssertBaseline();
        foreach (var key in new[] { Key.PageDown, Key.PageUp })
        {
            await dialog.HandleKeyAsync(key);
            queue.Drain();
            AssertBaseline();
        }

        void AssertBaseline()
        {
            Assert.Equal("Decision", dialog.Title);
            Assert.Equal(actions, dialog.Actions);
            Assert.Equal(banner, dialog.BannerText);
            Assert.Equal(hint, dialog.HintText);
            Assert.Equal(lines, dialog.Lines);
        }
    }

    private static async Task AssertGateBaseline(OwnerConsoleFullScreenView view, OwnerConsoleGateProgress? gate)
    {
        await view.HandleKeyAsync(new Key('g'));
        AssertBaseline();
        foreach (var key in new[] { Key.Home, Key.End, Key.PageDown, Key.PageUp, Key.Enter })
        {
            await view.HandleKeyAsync(key);
            AssertBaseline();
        }

        void AssertBaseline()
        {
            var dialog = view.GateView;
            Assert.Equal("GATE", dialog.Title);
            Assert.Equal(OwnerConsoleGateLayout.Lines(gate,
                OwnerConsoleEpicFormatter.Width(dialog.BodyPane.Viewport.Width), dialog.Expanded), dialog.Lines);
        }
    }

    private static void AssertGateTitle(OwnerConsoleGateDialog dialog, int columns) =>
        Assert.Equal(OwnerConsoleOverflow.Title("GATE", OwnerConsoleOverflow.Of(dialog.Lines.Count,
            dialog.BodyPane.Viewport.Y, dialog.BodyPane.Viewport.Height), columns), dialog.Title);

    private static void AssertEpicTitle(OwnerConsoleEpicDialog dialog, int columns) =>
        Assert.Equal(OwnerConsoleOverflow.Title("Epics", OwnerConsoleOverflow.Of(dialog.Lines.Count,
            dialog.BodyPane.Viewport.Y, dialog.BodyPane.Viewport.Height), columns), dialog.Title);

    private static void AssertEpicBaseline(OwnerConsoleEpicDialog expected, OwnerConsoleEpicDialog actual)
    {
        Assert.Equal("Epics", actual.Title);
        Assert.Equal(expected.HeaderLine, actual.HeaderLine);
        Assert.Equal(expected.HintText, actual.HintText);
        Assert.Equal(expected.Lines, actual.Lines);
    }

    private static void AssertPage(OwnerConsoleTextPage expected, OwnerConsoleTextPage actual)
    {
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.Equal(expected.Offset, actual.Offset);
    }

    private static OwnerConsoleDecision Decision(string text) =>
        new("q1", 1, "goal", "goal", OwnerQuestionKind.HumanInput, text, text, null, null, "ship");

    private static OwnerConsoleDecisionDetail Detail(OwnerConsoleDecision decision) => new(decision, TimeZoneInfo.Utc,
        (_, _, _) => Task.CompletedTask, _ => Task.FromResult<OwnerQuestionResolution?>(null), _ => Task.CompletedTask);

    private static OwnerConsoleViewModel Model(OwnerConsoleGateProgress? gate) =>
        new(new(true, 0, 0, 0, null, 0), [], [], [], Gate: gate);

    private static OwnerConsoleGateProgress Gate(int count) => new(true, "solo", [new("11111111", "")],
        DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5), Enumerable.Range(1, count).Select(i =>
            new OwnerConsoleGateLane(count == 2 ? (i == 1 ? "Passed lane" : "cli [trusted main baseline]") : $"Passed lane {i}",
                OwnerConsoleGateLaneState.Done, "passed", "local",
                TimeSpan.FromSeconds(10), 20, false, IsManifestLane: i != count)).ToArray(), count - 1, 0, count - 1, 1);

    private sealed class GateFixture : IDisposable
    {
        private readonly IApplication _app = Application.Create();
        internal readonly OwnerConsoleFullScreenView View;
        internal GateFixture()
        {
            var harness = new OwnerConsoleHarness();
            var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(), harness.State,
                harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            View = new(_app, controller, () => Task.CompletedTask);
        }
        public void Dispose() { View.Dispose(); _app.Dispose(); }
    }

    private sealed class UiDispatchQueue
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        internal void Enqueue(Action action) => _pending.Enqueue(action);
        internal void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
