using System.Collections.Concurrent;
using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;

// Parallel-safe: fixture-owned registry/databases/lease and instance-only terminal screen.
public sealed class OwnerConsoleOnboardingBoardTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] // Hang detector for preview publication, never a timing verdict.
    public async Task AllProjects_RendersBoldOnboardingHeaderBesideRunningProject_OutsideFooter()
    {
        using var fixture = new OnboardingConsoleFixture();
        var onboarding = fixture.Register("conduct-tail");
        var running = fixture.Register("running");
        OnboardingConsoleFixture.Store(onboarding, OnboardingConsoleFixture.Model(
            OnboardingConsoleFixture.TestKey, OnboardingConsoleFixture.BuildKey));
        OnboardingConsoleFixture.Store(running, OnboardingConsoleFixture.Model());
        OnboardingConsoleFixture.InstallManifest(running);
        fixture.StartConductor(running);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        harness.AddGoal("11111111-running", "Running work", AgentRole.Developer);
        Directory.CreateDirectory(Path.GetDirectoryName(running.SqliteStatePath)!);
        _ = StateDbMigrations.EnsureUpToDate(running.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(running.SqliteStatePath).SaveAsync(harness.Kernel, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(onboarding.SqliteStatePath), "Onboarding fixture must exercise the no-state-database path.");
        var source = new OwnerConsoleBoardSource("running", OwnerConsoleProjectScope.All,
            () => [OnboardingConsoleFixture.Describe(onboarding), OnboardingConsoleFixture.Describe(running)], fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), fixture.Clock);
        var model = builder.WithDecisions(await source.ComposeAsync(await builder.BuildBoardAsync(), TestContext.Current.CancellationToken), [], 0);
        var header = Assert.Single(model.Board.Where(row => row.Project == "conduct-tail"));
        Assert.True(header.IsDiagnostic);
        Assert.Equal("onboarding", header.State);
        Assert.Equal("conduct-tail · onboarding · declared · 2 questions open", header.Title);
        Assert.Equal("running", Assert.Single(model.Board.Where(row => row.Project == "running" && row.IsDiagnostic)).State);
        Assert.Empty(OwnerConsoleBoardSection.Footer(model));
        Assert.Equal(model.Board.Length, OwnerConsoleBoardSection.DisplayedRowCount(model.Board, true));

        using var app = Application.Create();
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, fixture.Clock);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask,
            token: TestContext.Current.CancellationToken, clock: fixture.Clock, projectLabel: "running", boardSource: source);
        view.Render(model);
        await TestHangGuard.WaitAsync(view.PreviewLoad, "onboarding board preview publication");
        using var screen = Application.Create();
        OwnerConsoleFrameOutputInstaller.RequireFactoryMember(screen.GetType()).SetValue(screen, new ScreenFactory());
        screen.Init();
        Assert.NotNull(screen.Driver);
        if (screen.Driver.AnsiStartupGate is { } startup)
            foreach (var query in startup.PendingQueries.ToArray()) startup.MarkComplete(query);
        var session = screen.Begin(view.Window);
        try
        {
            screen.LayoutAndDraw(forceRedraw: true);
            var bounds = view.BoardPane.FrameToScreen();
            bounds.Size = new Size(view.BoardPane.Viewport.Width, view.BoardPane.SuperView!.Viewport.Height);
            var cells = screen.Driver.Contents!;
            var lines = Enumerable.Range(0, bounds.Height).Select(y => string.Concat(
                Enumerable.Range(0, bounds.Width).Select(x =>
                {
                    var grapheme = cells[bounds.Y + y, bounds.X + x].Grapheme;
                    return string.IsNullOrEmpty(grapheme) ? " " : grapheme;
                }))).ToArray();
            var lineIndex = Assert.Single(Enumerable.Range(0, lines.Length)
                .Where(index => lines[index].Contains(OwnerConsoleBoardSection.HeaderLine(header), StringComparison.Ordinal)));
            var start = lines[lineIndex].IndexOf("──", StringComparison.Ordinal);
            for (var x = start; x < start + OwnerConsoleBoardSection.HeaderLine(header).Length; x++)
            {
                var attribute = cells[bounds.Y + lineIndex, bounds.X + x].Attribute;
                Assert.NotNull(attribute);
                Assert.True(attribute.Value.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Bold));
            }
            Assert.Contains(lines, line => line.Contains("running · conductor running", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("stopped: conduct-tail", StringComparison.Ordinal));
        }
        finally { screen.End(session); }
    }

    [Fact]
    public async Task InstalledWithoutConductor_RemainsInBoard_WithPersistedCrosswalkStack()
    {
        using var fixture = new OnboardingConsoleFixture();
        var project = fixture.Register("price-selector");
        var model = OnboardingConsoleFixture.Model() with
        {
            Stack = ProjectStack.Crosswalk,
            EnvironmentNeeds = [new("crosswalk-stack", new("jvm", OnboardingConsoleFixture.Source, FactConfidence.High))]
        };
        OnboardingConsoleFixture.Store(project, model);
        OnboardingConsoleFixture.InstallManifest(project);
        var harness = new OwnerConsoleHarness(fixture.Clock);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), fixture.Clock);
        var source = new OwnerConsoleBoardSource("price-selector", OwnerConsoleProjectScope.All,
            () => [OnboardingConsoleFixture.Describe(project)], fixture.Clock);
        var board = await source.ComposeAsync(await builder.BuildBoardAsync(), TestContext.Current.CancellationToken);
        var header = Assert.Single(board.Board);
        Assert.Equal("price-selector · onboarding · jvm via crosswalk · manifest installed · conductor not started", header.Title);
        Assert.Equal(1, OwnerConsoleBoardSection.DisplayedRowCount(board.Board, true));
        Assert.Empty(OwnerConsoleBoardSection.Footer(board));
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
