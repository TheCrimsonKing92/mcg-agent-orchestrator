using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

// Parallel-safe: fake state/clock and fixture-owned terminal instances; no global console or files.
public sealed class OwnerConsoleBoardCollapseTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Preview publication hang detector.
    public async Task Collapse_TenHomeGoals_RevealsOtherGoalAndToggleRestoresRows()
    {
        using var scene = new Scene(10);
        await scene.FocusBoardAsync();
        using var screen = new Screen(scene.View);
        Assert.Equal(0, scene.View.BoardPane.RowOffset);
        Assert.DoesNotContain(screen.Lines(), line => line.Contains("eeeeeeee", StringComparison.Ordinal));
        Assert.Equal(13, scene.View.BoardTable.Rows.Count);
        Assert.Contains("c collapse", scene.View.HintText);

        var key = new Key('c');
        await scene.View.HandleKeyAsync(key);

        Assert.True(key.Handled);
        AssertHeaderSelected(scene, "home", 0);
        Assert.Equal(3, scene.View.BoardTable.Rows.Count);
        Assert.Single(scene.Model.Board.Where(row => row.Project == "home"));
        Assert.True(scene.Model.Board[0].Collapsed);
        Assert.Equal(2, Assert.IsType<OwnerConsoleBoardPane>(scene.View.BoardPane).Headers.Count);
        Assert.Empty(OwnerConsoleBoardSection.Footer(scene.Model));
        var collapsedLines = screen.Lines();
        Assert.Equal(0, scene.View.BoardPane.RowOffset);
        Assert.Single(collapsedLines.Where(line => line.Contains("── home ·", StringComparison.Ordinal)));
        Assert.DoesNotContain(collapsedLines, line => line.Contains("Home goal", StringComparison.Ordinal));
        Assert.Contains(collapsedLines, line => line.Contains("eeeeeeee", StringComparison.Ordinal));

        await scene.View.HandleKeyAsync(new Key('C'));

        AssertHeaderSelected(scene, "home", 0);
        Assert.Equal(13, scene.View.BoardTable.Rows.Count);
        Assert.Equal(10, scene.Model.Board.Count(row => row.Project == "home" && !row.IsDiagnostic));
        Assert.False(scene.Model.Board[0].Collapsed);
        Assert.Contains(screen.Lines(), line => line.Contains("Home goal", StringComparison.Ordinal));
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(scene.Base.Board.First(row => !row.IsDiagnostic).GoalId, scene.View.SelectedGoalId);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Preview publication hang detector.
    public async Task Refresh_NewDecision_UpdatesCollapsedHeaderAndSkipsHiddenGoals()
    {
        using var scene = new Scene(2, homeFirst: false);
        await scene.FocusBoardAsync();
        await scene.View.HandleKeyAsync(Key.End);
        await scene.View.HandleKeyAsync(new Key('c'));
        AssertHeaderSelected(scene, "home", 2);
        using var screen = new Screen(scene.View);
        var decision = new OwnerConsoleDecision("home-q", 1, scene.HomeIds[0], scene.HomeIds[0][..8],
            OwnerQuestionKind.HumanInput, "Choose", "Choose", null, null, null);

        scene.View.Render(scene.Builder.WithDecisions(scene.Base, [decision], 0));

        AssertHeaderSelected(scene, "home", 0);
        var header = Assert.Single(scene.Model.Board.Where(row => row.Project == "home"));
        Assert.True(header.Collapsed);
        Assert.Equal(1, header.OwnerDecisions);
        Assert.EndsWith(" · 1 decision ──", Assert.IsType<OwnerConsoleBoardPane>(scene.View.BoardPane).Headers[0]);
        Assert.Contains(screen.Lines(), line => line.Contains(" · 1 decision ──", StringComparison.Ordinal));
        Assert.Equal(3, scene.View.BoardTable.Rows.Count);
        Assert.DoesNotContain(scene.Model.Board, row => scene.HomeIds.Contains(row.GoalId));

        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("eeeeeeee-other", scene.View.SelectedGoalId);
        await scene.View.HandleKeyAsync(Key.CursorUp);
        AssertHeaderSelected(scene, "home", 0);
        foreach (var key in new[] { Key.End, Key.CursorDown, Key.CursorUp, Key.PageDown, Key.PageUp, Key.Home })
        {
            await scene.View.HandleKeyAsync(key);
            Assert.DoesNotContain(scene.View.SelectedGoalId, scene.HomeIds);
            var selected = scene.Model.Board[scene.View.BoardPane.Value!.SelectedCell.Y];
            Assert.True(selected.Collapsed || selected.GoalId == "eeeeeeee-other");
        }
        AssertHeaderSelected(scene, "home", 0);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Preview publication hang detector.
    public async Task ScopeChange_SingleProjectIgnoresCollapseAndSessionRetainsIt()
    {
        using var scene = new Scene(2);
        await scene.FocusBoardAsync();
        await scene.View.HandleKeyAsync(new Key('c'));
        var single = scene.Base with
        {
            Board = [.. scene.Base.Board.Where(row => row.Project == "home" && !row.IsDiagnostic)],
            BoardScope = new("home", 1, "home")
        };
        scene.View.Render(single);
        Assert.Equal(2, scene.View.BoardTable.Rows.Count);
        Assert.DoesNotContain("c collapse", scene.View.HintText);
        await scene.View.HandleKeyAsync(new Key('c'));
        Assert.Equal(single.Board, scene.Model.Board);

        scene.View.Render(scene.Base);
        Assert.True(Assert.Single(scene.Model.Board.Where(row => row.Project == "home")).Collapsed);
        using var fresh = new Scene(2);
        Assert.Equal(2, fresh.Model.Board.Count(row => row.Project == "home" && !row.IsDiagnostic));
        Assert.All(fresh.Model.Board, row => Assert.False(row.Collapsed));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Preview publication hang detector.
    public async Task Collapse_OnboardingSection_RendersOnlyHeaderAndCanExpand()
    {
        using var scene = new Scene(1);
        var model = scene.Base with
        {
            Board = [.. scene.Base.Board.Select(row => row.Project == "home" && row.IsDiagnostic
                ? row with { State = "onboarding", Title = "home · onboarding · 1 question open" } : row)]
        };
        scene.View.Render(model);
        await scene.FocusBoardAsync();
        await scene.View.HandleKeyAsync(new Key('c'));
        AssertHeaderSelected(scene, "home", 0);
        Assert.Single(scene.Model.Board.Where(row => row.Project == "home"));
        Assert.Empty(OwnerConsoleBoardSection.Footer(scene.Model));
        await scene.View.HandleKeyAsync(new Key('c'));
        AssertHeaderSelected(scene, "home", 0);
        Assert.Equal(2, scene.Model.Board.Count(row => row.Project == "home"));
    }

    private static void AssertHeaderSelected(Scene scene, string project, int index)
    {
        Assert.Null(scene.View.SelectedGoalId);
        Assert.Equal(project, scene.View.SelectedProject);
        Assert.Equal(index, scene.View.BoardPane.Value!.SelectedCell.Y);
        Assert.True(scene.Model.Board[index].IsDiagnostic);
    }

    private sealed class Scene : IDisposable
    {
        private readonly IApplication _app = Application.Create();
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly OwnerConsoleViewModel Base;
        internal readonly string[] HomeIds;
        internal OwnerConsoleViewModel Model => Controller.Model!;

        internal Scene(int homeGoals, bool homeFirst = true)
        {
            var harness = new OwnerConsoleHarness();
            HomeIds = Enumerable.Range(1, homeGoals).Select(index => $"{index:00000000}-home").ToArray();
            foreach (var id in HomeIds) harness.AddGoal(id, "Home goal " + id, AgentRole.Developer);
            Builder = new(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
            Controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(),
                harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            View = new(_app, Controller, () => Task.CompletedTask,
                token: TestContext.Current.CancellationToken, clock: harness.Clock);
            var home = OwnerConsoleBoardSection.Create("home", HomeIds.Select(id =>
                new OwnerConsoleBoardRow(id[..8], "", "Home goal " + id, "Active", "working", null, id)
                    { Project = "home" }).ToImmutableArray(), false, null, harness.Clock.GetUtcNow());
            var other = OwnerConsoleBoardSection.Create("other",
                [new("eeeeeeee", "", "Other goal", "Active", "working", null, "eeeeeeee-other")
                    { Project = "other", IsForeign = true }], false, null, harness.Clock.GetUtcNow());
            string[] projects = homeFirst ? ["home", "other"] : ["other", "home"];
            Base = new(new(false, homeGoals + 1, 0, 0, null, 0), [],
                OwnerConsoleBoardSection.Flatten(homeFirst ? [home, other] : [other, home]), [],
                BoardScope: new("all", 2, "home", projects));
            View.Render(Base);
        }

        internal async Task FocusBoardAsync()
        {
            await TestHangGuard.WaitAsync(View.PreviewLoad, "collapse board preview publication");
            View.FocusDecisions();
            await View.HandleKeyAsync(Key.Tab);
            Assert.Equal(OwnerConsolePane.Board, View.FocusedPane);
        }

        public void Dispose() { View.Dispose(); _app.Dispose(); }
    }

    private sealed class Screen : IDisposable
    {
        private readonly IApplication _app = Application.Create();
        private readonly OwnerConsoleFullScreenView _view;
        private readonly SessionToken _session;

        internal Screen(OwnerConsoleFullScreenView view)
        {
            _view = view;
            OwnerConsoleFrameOutputInstaller.RequireFactoryMember(_app.GetType()).SetValue(_app, new ScreenFactory());
            _app.Init();
            Assert.NotNull(_app.Driver);
            Assert.Equal(new Size(160, 40), _app.Driver.Screen.Size);
            if (_app.Driver.AnsiStartupGate is { } startup)
                foreach (var query in startup.PendingQueries.ToArray()) startup.MarkComplete(query);
            _session = _app.Begin(view.Window);
            view.BoardPane.SetFocus();
        }

        internal string[] Lines()
        {
            _app.LayoutAndDraw(forceRedraw: true);
            var bounds = _view.BoardPane.FrameToScreen();
            var cells = _app.Driver!.Contents!;
            return Enumerable.Range(0, _view.BoardPane.Viewport.Height).Select(y => string.Concat(
                Enumerable.Range(0, _view.BoardPane.Viewport.Width).Select(x =>
                {
                    var grapheme = cells[bounds.Y + y, bounds.X + x].Grapheme;
                    return string.IsNullOrEmpty(grapheme) ? " " : grapheme;
                }))).ToArray();
        }

        public void Dispose() { _app.End(_session); _app.Dispose(); }
    }

    private sealed class ScreenFactory() : AnsiComponentFactory(
        output: new OwnerConsoleFrameReproductionTests.RecordingOutput(160, 40) { RecordOnly = true },
        sizeMonitor: new FixedSizeMonitor())
    {
        public override IInput<char> CreateInput() => new NoInput();
    }

    private sealed class NoInput : IInput<char>
    {
        public CancellationTokenSource? ExternalCancellationTokenSource { get; set; }
        public void Initialize(ConcurrentQueue<char> inputBuffer) { }
        public void Run(CancellationToken cancellationToken) => cancellationToken.WaitHandle.WaitOne();
        public void Dispose() { }
    }

    private sealed class FixedSizeMonitor : ISizeMonitor
    {
        public bool InitialSizeReceived => true;
        public int InitialCursorRow => 0;
        public event EventHandler<SizeChangedEventArgs>? SizeChanged { add { } remove { } }
        public void Initialize(IDriver driver) => driver.SetScreenSize(160, 40);
        public bool Poll() => false;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
    }
}
