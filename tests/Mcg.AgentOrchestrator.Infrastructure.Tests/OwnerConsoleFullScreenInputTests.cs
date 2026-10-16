using System.Drawing;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

// Parallel-safe: instance apps remain uninitialized; clocks, input and trace streams are local.
public sealed class OwnerConsoleFullScreenInputTests
{
    [Fact]
    public void TraceUsesInjectedTicksAndCorrelatesNestedWork()
    {
        var clock = new TraceClock();
        var trace = new OwnerConsoleInputDiagnostics(clock);
        using (trace.Begin("keyboard-callback", detail: "Down", input: true))
        {
            clock.Tick = 3;
            using (trace.Begin("fit")) { clock.Tick = 8; }
            clock.Tick = 13;
        }
        using var receipt = Export(trace);
        var records = Records(receipt);
        Assert.Equal([0L, 3L, 8L, 13L], records.Select(record => record.Timestamp));
        Assert.All(records, record => Assert.Equal(1, record.Input));
        Assert.Equal(records[0].Span, records[1].Parent);
        Assert.Equal(records[1].Span, records[2].Span);
        Assert.Equal(1000, receipt.RootElement.GetProperty("TimestampFrequency").GetInt64());
        Assert.Empty(receipt.RootElement.GetProperty("IncompleteSpans").EnumerateArray());
    }

    [Fact]
    public void OverflowAndUnfinishedWorkAreExplicitAndBounded()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock(), capacity: 2);
        using var input = trace.Begin("keyboard-callback", input: true);
        trace.Mark("selection");
        trace.Mark("overflow");
        using var receipt = Export(trace);
        Assert.Equal(2, Records(receipt).Length);
        Assert.Equal(1, receipt.RootElement.GetProperty("DroppedRecords").GetInt64());
        Assert.Equal(expected: Records(receipt)[0].Span,
            actual: receipt.RootElement.GetProperty("IncompleteSpans")[0].GetInt64());
        Assert.Contains(receipt.RootElement.GetProperty("MeasurementLimits").EnumerateArray(),
            limit => limit.GetString()!.Contains("not physical input"));
        Assert.Contains(receipt.RootElement.GetProperty("MeasurementLimits").EnumerateArray(),
            limit => limit.GetString()!.Contains("not terminal flush"));
    }

    [Fact]
    public async Task OverlappingAsyncCallbacksKeepSeparateInputIdentities()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Callback(string key)
        {
            using var input = trace.Begin("keyboard-callback", detail: key, input: true);
            await release.Task;
            using var handler = trace.Begin("handler");
        }
        var first = Callback("first");
        var second = Callback("second");
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30)); // hang-only: callback release did not complete
        trace.Mark("outside-input");
        using var receipt = Export(trace);
        var records = Records(receipt);
        Assert.Equal(0, records[^1].Input);
        var roots = records.Where(record => record.Phase == "keyboard-callback" && record.Boundary == "begin").ToArray();
        Assert.Equal([1L, 2L], roots.Select(record => record.Input));
        foreach (var root in roots)
            Assert.Contains(records, record => record.Phase == "handler" && record.Boundary == "begin" &&
                record.Input == root.Input && record.Parent == root.Span);
        Assert.Empty(receipt.RootElement.GetProperty("IncompleteSpans").EnumerateArray());
    }

    [Fact]
    public void QueuedCallbacksRetainPostingIdentityAndSeparateQueueFromExecution()
    {
        var clock = new TraceClock();
        var trace = new OwnerConsoleInputDiagnostics(clock);
        Action callback;
        var executed = false;
        using (trace.Begin("keyboard-callback", input: true))
            callback = trace.ObservePost("refresh-render", () => { executed = true; clock.Tick = 9; });
        clock.Tick = 7;
        callback();
        _ = trace.ObservePost("never-executed", () => throw new InvalidOperationException());
        using var receipt = Export(trace);
        Assert.True(executed);
        var records = Records(receipt);
        var queued = records.Where(record => record.Phase == "refresh-render").ToArray();
        Assert.Equal(["posted", "begin", "end"], queued.Select(record => record.Boundary));
        Assert.Equal([0L, 7L, 9L], queued.Select(record => record.Timestamp));
        Assert.All(queued, record => { Assert.Equal(1, record.Input); Assert.Equal(queued[0].Span, record.Span); });
        Assert.Equal(records.Single(record => record.Phase == "never-executed").Span,
            receipt.RootElement.GetProperty("IncompleteSpans")[0].GetInt64());
    }

    [Fact]
    public async Task OffsetViewportChangesAreRecordedSeparatelyFromGeometry()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(diagnostics: trace);
        Populate(scene, 20);
        await scene.Render([]);
        SetFrames(scene, 5);
        await scene.View.HandleKeyAsync(Key.PageDown);
        var before = scene.Controller.SelectedDecisionId;
        var viewport = scene.View.DecisionsPane.Viewport;
        scene.View.DecisionsPane.Viewport = new Rectangle(viewport.X, 3, viewport.Width, viewport.Height);
        Assert.Equal(before, scene.Controller.SelectedDecisionId);
        using var receipt = Export(trace);
        Assert.Contains(Records(receipt), record => record.Phase == "decisions-viewport" &&
            record.State!.Decisions.Y == 3 && record.State.Decisions.Width == viewport.Width &&
            record.State.Decisions.Height == viewport.Height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedMixedPagingPreservesEveryPaneWithOrWithoutDiagnostics(bool enabled)
    {
        var trace = enabled ? new OwnerConsoleInputDiagnostics(new TraceClock()) : null;
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(diagnostics: trace);
        Populate(scene, 20);
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render(Enumerable.Range(0, 30).Select(index =>
            new OwnerConductEvent(time.AddSeconds(index), "acceptance", "00000000", "result=passed")).ToArray());
        SetFrames(scene, 5);
        var keys = new[] { Key.End, Key.Home, Key.CursorDown, Key.PageDown, Key.CursorUp, Key.PageUp, Key.End, Key.Home };
        for (var pane = 0; pane < 3; pane++)
        {
            var count = pane == 2 ? scene.View.ActivityLines.Count : 20;
            var expected = new[] { count - 1, 0, 1, 6, 5, 0, count - 1, 0 };
            for (var index = 0; index < keys.Length; index++)
            {
                await scene.View.HandleKeyAsync(new Key(keys[index]));
                Assert.Equal(expected[index], Selection(scene, pane));
                Assert.Equal((OwnerConsolePane)pane, scene.View.FocusedPane);
            }
            await scene.View.HandleKeyAsync(Key.Tab);
        }
        await scene.View.HandleKeyAsync(Key.Tab.WithShift);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
        await scene.View.HandleKeyAsync(new Key(':'));
        await scene.View.HandleKeyAsync(Key.Esc);
        Assert.Equal(OwnerConsolePane.Activity, scene.View.FocusedPane);
        if (trace is null) return;
        using var receipt = Export(trace);
        var records = Records(receipt);
        var inputs = records.Where(record => record.Phase == "headless-input" && record.Boundary == "begin").ToArray();
        Assert.Equal(30, inputs.Length);
        Assert.Equal(Enumerable.Range(1, 30).Select(index => (long)index), inputs.Select(record => record.Input));
        var ends = records.Where(record => record.Phase == "handler" && record.Boundary == "end").ToArray();
        Assert.All(ends, record => Assert.Equal("handled", record.Detail));
        Assert.Equal(19, ends[0].State!.Decisions.Index);
        Assert.Equal(6, ends[3].State!.Decisions.Index);
        Assert.Contains(records, record => record.Phase == "board-source-replacement");
        Assert.Contains(records, record => record.Phase == "activity-source-replacement");
    }

    [Fact]
    public async Task ExcludedModalKeysAndIgnoredKeysAreRecordedWithoutRouting()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(diagnostics: trace);
        Populate(scene, 3);
        await scene.Render([]);
        await scene.View.ObserveKeyAsync(Key.End, false, "keyboard-callback");
        Assert.Equal(0, scene.Controller.SelectedIndex);
        await scene.View.HandleKeyAsync(new Key('z'));
        using var receipt = Export(trace);
        var records = Records(receipt);
        Assert.Contains(records, record => record.Input == 1 && record.Detail == "excluded-top-window");
        Assert.DoesNotContain(records, record => record.Input == 1 && record.Phase == "handler");
        Assert.Contains(records, record => record.Input == 2 && record.Phase == "handler" && record.Detail == "unhandled");
    }

    [Fact]
    public async Task ResizeAndRefreshRecordsRetainSelectedIdentityAndUpdatedGeometry()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(diagnostics: trace);
        Populate(scene, 12);
        await scene.Render([]);
        SetFrames(scene, 5);
        await scene.View.HandleKeyAsync(Key.PageDown);
        Assert.Equal("q5", scene.Controller.SelectedDecisionId);
        scene.Harness.Questions.Items.Insert(0, new("new", "00000000", OwnerQuestionKind.HumanInput, "New question"));
        await scene.Render([]);
        Assert.Equal("q5", scene.Controller.SelectedDecisionId);
        Assert.Equal(6, scene.Controller.SelectedIndex);
        scene.View.DecisionsPane.Frame = new Rectangle(0, 0, 180, 7);
        Assert.Equal("q5", scene.Controller.SelectedDecisionId);
        using var receipt = Export(trace);
        var records = Records(receipt);
        Assert.Contains(records, record => record.Phase == "decisions-viewport" && record.State!.Decisions.Width == 180 &&
            record.State.Decisions.Height == 7);
        Assert.Contains(records, record => record.Phase == "render" && record.Boundary == "end" &&
            record.State!.Decisions.Identity == "q5" && record.State.Decisions.Index == 6);
    }

    [Fact]
    public async Task EmptyPanesAndMinimumPageSizeKeepBoundarySelections()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene(diagnostics: trace);
        await scene.Render([]);
        for (var pane = 0; pane < 3; pane++)
        {
            await scene.View.HandleKeyAsync(Key.PageDown);
            await scene.View.HandleKeyAsync(Key.End);
            Assert.Equal(-1, Selection(scene, pane));
            await scene.View.HandleKeyAsync(Key.Tab);
        }
        Populate(scene, 3);
        await scene.Render([]);
        SetFrames(scene, 0);
        await scene.View.HandleKeyAsync(Key.PageDown);
        Assert.Equal(1, scene.Controller.SelectedIndex);
        await scene.View.HandleKeyAsync(Key.Home);
        await scene.View.HandleKeyAsync(Key.CursorUp);
        Assert.Equal(0, scene.Controller.SelectedIndex);
        using var receipt = Export(trace);
        Assert.Contains(Records(receipt), record => record.Phase == "handler" && record.Boundary == "end" &&
            record.State!.Decisions.Count == 0 && record.State.Decisions.Index == -1);
    }

    [Fact]
    public void TraceExportFailurePropagatesRatherThanReturningAReceipt()
    {
        var trace = new OwnerConsoleInputDiagnostics(new TraceClock());
        trace.Mark("sample");
        using var stream = new FailedExportStream();
        Assert.Throws<IOException>(() => trace.Export(stream));
    }

    private static void Populate(OwnerConsoleActivityOutcomeTests.Scene scene, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var id = index.ToString("D8");
            scene.Harness.AddGoal(id, "Goal " + index, AgentRole.Developer);
            scene.Harness.Questions.Items.Add(new("q" + index, id, OwnerQuestionKind.HumanInput, "Question " + index));
        }
    }

    private static void SetFrames(OwnerConsoleActivityOutcomeTests.Scene scene, int height)
    {
        scene.View.DecisionsPane.Frame = new Rectangle(0, 0, 200, height);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, height);
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, height + headers);
    }

    private static int Selection(OwnerConsoleActivityOutcomeTests.Scene scene, int pane) => pane == 0 ?
        scene.Controller.SelectedIndex : pane == 1 ? scene.View.SelectedGoalId is null ? -1 : scene.View.BoardPane.Value?.SelectedCell.Y ?? -1 :
        scene.View.ActivityPane.SelectedItem ?? -1;

    private static JsonDocument Export(OwnerConsoleInputDiagnostics trace)
    {
        using var stream = new MemoryStream();
        trace.Export(stream);
        return JsonDocument.Parse(stream.ToArray());
    }

    private static OwnerConsoleInputTraceRecord[] Records(JsonDocument receipt) =>
        receipt.RootElement.GetProperty("Records").Deserialize<OwnerConsoleInputTraceRecord[]>()!;

    private sealed class TraceClock : TimeProvider
    {
        internal long Tick { get; set; }
        public override long GetTimestamp() => Tick;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FailedExportStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("trace destination unavailable");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("trace destination unavailable");
    }
}
