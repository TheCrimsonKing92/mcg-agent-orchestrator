using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

// Parallel-safe: instance-owned controls, uninitialized applications and explicitly gated I/O delegates.
public sealed class OwnerConsoleLastGoodRenderTests
{
    private const string FirstId = "11111111-first";
    private const string SecondId = "22222222-second";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void ActivityRender_IdenticalModel_ReplacesSourceZeroTimes()
    {
        var pane = new OwnerConsoleActivityPane();
        using var list = pane.List;
        list.Frame = new Rectangle(0, 0, 100, 2);
        var model = Model() with
        {
            Activity = [.. Enumerable.Range(0, 4).Select(index => new OwnerConsoleActivityItem(
                At.AddSeconds(-index), "goal-lifecycle", "goal", "11111111", "Event " + index,
                Phrase: "Event " + index))]
        };
        pane.Render(model, 100);

        AssertIdenticalRender(list, pane.Source, () => pane.Render(model, 100));
        pane.Inspect(2);
        var selectedKey = pane.Keys[2];
        pane.Render(model with { Activity = [model.Activity[0] with { Phrase = "Changed event" }, .. model.Activity.Skip(1)] }, 100);

        Assert.Equal(2, pane.Source.Replacements);
        Assert.Contains(pane.Lines, line => line.Contains("Changed event", StringComparison.Ordinal));
        Assert.Equal(selectedKey, pane.Keys[list.SelectedItem!.Value]);
    }

    [Fact]
    public void DecisionsRender_IdenticalModel_ReplacesSourceZeroTimes()
    {
        var pane = new OwnerConsoleDecisionsPane();
        using var list = pane.List;
        using var label = pane.EmptyLabel;
        list.Frame = new Rectangle(0, 0, 100, 2);
        var model = Model() with
        {
            Decisions = [.. Enumerable.Range(1, 4).Select(number => new OwnerConsoleDecision(
                "q" + number, number, FirstId, "11111111", OwnerQuestionKind.HumanInput,
                "Question " + number, "Question " + number, null, null, null))]
        };
        pane.Render(model, 2, 100);

        AssertIdenticalRender(list, pane.Source, () => pane.Render(model, 2, 100));
        pane.Render(model with { Decisions = [model.Decisions[0] with { Summary = "Changed question" }, .. model.Decisions.Skip(1)] }, 2, 100);

        Assert.Equal(2, pane.Source.Replacements);
        Assert.Contains(pane.Lines, line => line.Contains("Changed question", StringComparison.Ordinal));
        Assert.Equal(2, list.SelectedItem);
    }

    [Fact]
    public void GateRender_IdenticalProgress_ReplacesSourceZeroTimes()
    {
        using var dialog = new OwnerConsoleGateDialog();
        dialog.BodyPane.Frame = new Rectangle(0, 0, 100, 2);
        var gate = new OwnerConsoleGateProgress(true, "solo", [new("11111111", "First goal")],
            At, TimeSpan.FromMinutes(1),
            [new("Console tests", OwnerConsoleGateLaneState.Running, "running", "local", TimeSpan.FromSeconds(10), 20, false)],
            0, 10, 0, 1);
        dialog.Open(OwnerConsolePane.Board, gate);

        AssertIdenticalRender(dialog.BodyPane, dialog.SourceRenderer, () => dialog.Render(gate));
        dialog.Render(gate with { Lanes = [gate.Lanes[0] with { Name = "Changed lane" }] });

        Assert.Equal(2, dialog.SourceRenderer.Replacements);
        Assert.Contains(dialog.Lines, line => line.Contains("Changed lane", StringComparison.Ordinal));
        Assert.Equal(2, dialog.SelectedLine);
    }

    [Fact]
    public async Task Preview_SelectionChange_ShowsNewBoardRowDuringGatedLoad()
    {
        using var scene = new PreviewScene(SecondId, gatedRead: 1);
        await scene.LoadInitialAsync();
        Assert.Equal(string.Join('\n', "First summary title", "No committed changes yet", "Developer"), scene.View.PreviewText);
        scene.View.FocusDecisions();
        await scene.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);

        try
        {
            await scene.View.HandleKeyAsync(Key.CursorDown);
            await TestHangGuard.WaitAsync(scene.Entered.Task, "second goal summary read entered");

            Assert.Equal(SecondId, scene.View.SelectedGoalId);
            Assert.False(scene.Release.Task.IsCompleted);
            Assert.False(scene.View.PreviewLoad.IsCompleted);
            Assert.Equal(string.Join('\n', "Second board title", "question for you"), scene.View.PreviewText);
            Assert.DoesNotContain("First", scene.View.PreviewText);
            Assert.DoesNotContain("No committed changes yet", scene.View.PreviewText);
            Assert.DoesNotContain("Loading", scene.View.PreviewText, StringComparison.OrdinalIgnoreCase);
        }
        finally { scene.Release.TrySetResult(); }

        await TestHangGuard.WaitAsync(scene.View.PreviewLoad, "second goal summary publication");
        Assert.Equal(string.Join('\n', "Second summary title", "No committed changes yet", "question for you"), scene.View.PreviewText);
    }

    [Fact]
    public async Task Preview_SameGoalReread_KeepsExactLinesDuringGatedLoad()
    {
        using var scene = new PreviewScene(FirstId, gatedRead: 2);
        await scene.LoadInitialAsync();
        var previous = scene.View.PreviewText;
        Assert.Equal(string.Join('\n', "First summary title", "No committed changes yet", "Developer"), previous);
        var refreshed = scene.Model with
        {
            Board = [scene.Model.Board[0] with { Title = "Changed board title", Stage = "reviewing" }, scene.Model.Board[1]]
        };

        try
        {
            scene.View.Render(refreshed);
            await TestHangGuard.WaitAsync(scene.Entered.Task, "same goal summary reread entered");

            Assert.Equal(FirstId, scene.View.SelectedGoalId);
            Assert.False(scene.Release.Task.IsCompleted);
            Assert.False(scene.View.PreviewLoad.IsCompleted);
            Assert.Equal(previous, scene.View.PreviewText);
        }
        finally { scene.Release.TrySetResult(); }

        await TestHangGuard.WaitAsync(scene.View.PreviewLoad, "refreshed selected summary publication");
        Assert.Equal(string.Join('\n', "First summary title", "No committed changes yet", "reviewing"), scene.View.PreviewText);
    }

    private static void AssertIdenticalRender(ListView list, OwnerConsoleListRenderer renderer, Action render)
    {
        Assert.Equal(1, renderer.Replacements);
        Assert.True(list.Source!.Count > 3);
        list.SelectedItem = 2;
        list.Viewport = list.Viewport with { Y = 1 };
        Assert.Equal(2, list.SelectedItem);
        Assert.Equal(1, list.Viewport.Y);
        var source = list.Source;

        render();

        Assert.Equal(1, renderer.Replacements);
        Assert.Same(source, list.Source);
        Assert.Equal(2, list.SelectedItem);
        Assert.Equal(1, list.Viewport.Y);
    }

    private static OwnerConsoleViewModel Model() => new(new(false, 0, 0, 0, null, 0), [], [], []);

    private sealed class PreviewScene : IDisposable
    {
        private readonly OwnerConsoleScreenController _controller;
        private readonly IApplication _app;
        private readonly TaskCompletionSource _initialEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _initialRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OwnerConsoleFullScreenView View { get; }
        internal OwnerConsoleViewModel Model { get; } = OwnerConsoleLastGoodRenderTests.Model() with
        {
            Board =
            [
                new("11111111", "Console", "First board title", "Active", "Developer", null, FirstId),
                new("22222222", "Console", "Second board title", "Active", "question for you", null, SecondId)
            ]
        };

        internal PreviewScene(string gatedGoal, int gatedRead)
        {
            var harness = new OwnerConsoleHarness();
            harness.AddGoal(FirstId, "First summary title", AgentRole.Developer);
            harness.AddGoal(SecondId, "Second summary title", AgentRole.Developer);
            var reads = 0;
            var initialReads = 0;
            var summary = new OwnerGoalChangeSummaryQuery(promotion: async (id, token) =>
            {
                if (id == FirstId && Interlocked.Increment(ref initialReads) == 1)
                {
                    _initialEntered.TrySetResult();
                    await _initialRelease.Task.WaitAsync(token);
                }
                if (id == gatedGoal && Interlocked.Increment(ref reads) == gatedRead)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                return null;
            });
            _controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers,
                new Dialogs(), harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
                goalDetail: new OwnerGoalDetailQuery(harness.State, harness.Tail, summary: summary));
            _app = Application.Create();
            Assert.False(_app.Initialized);
            View = new OwnerConsoleFullScreenView(_app, _controller, () => Task.CompletedTask,
                token: TestContext.Current.CancellationToken, clock: harness.Clock);
            View.Window.Layout(new Size(120, 40));
        }

        internal async Task LoadInitialAsync()
        {
            View.Render(Model);
            try { await TestHangGuard.WaitAsync(_initialEntered.Task, "initial summary read entered"); }
            finally { _initialRelease.TrySetResult(); }
            await TestHangGuard.WaitAsync(View.PreviewLoad, "initial selected summary publication");
        }

        public void Dispose()
        {
            View.Dispose();
            _initialRelease.TrySetResult();
            Release.TrySetResult();
            _controller.Dispose();
            _app.Dispose();
        }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("No dialogs expected");
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes expected");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes expected");
    }
}
