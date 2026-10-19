using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: instance application, private state and recording seams; no terminal driver.
public sealed class OwnerConsoleKeyCommandScenarioTests
{
    [Fact]
    public async Task LandingsAndUnboundKeysWorkFromEveryPane()
    {
        using var scene = new Scene();
        await scene.Render();
        foreach (var pane in new[] { OwnerConsolePane.Decisions, OwnerConsolePane.Board, OwnerConsolePane.Activity })
        {
            Assert.Equal(pane, scene.View.FocusedPane);
            var calls = scene.Landings.Count;
            var dialogs = scene.Dialogs.Texts.Count;
            var landingsKey = new Key('l');
            await scene.View.HandleKeyAsync(landingsKey);
            Assert.True(landingsKey.Handled);
            Assert.Equal(new[] { "" }, scene.Landings.Skip(calls));
            Assert.Equal(("Landings", "delivered rows"), Assert.Single(scene.Dialogs.Texts.Skip(dialogs)));

            var conductorCalls = scene.Harness.Conductor.Calls.Count;
            var answerCalls = scene.Harness.Answers.Calls.Count;
            var unbound = new Key('x');
            await scene.View.HandleKeyAsync(unbound);
            Assert.False(unbound.Handled);
            Assert.Equal(calls + 1, scene.Landings.Count);
            Assert.Equal(dialogs + 1, scene.Dialogs.Texts.Count);
            Assert.Equal(conductorCalls, scene.Harness.Conductor.Calls.Count);
            Assert.Equal(answerCalls, scene.Harness.Answers.Calls.Count);
            Assert.Empty(scene.Dialogs.Confirmations);
            Assert.Empty(scene.Dialogs.Prompts);
            var notice = Assert.Single(scene.View.NoticeStrip.Visible);
            Assert.Equal("x: not bound; press : for commands or ? for help", notice.Text);
            Assert.Equal(OwnerConsoleNoticeSeverity.Failure, notice.Severity);
            Assert.Equal(OwnerConsoleNoticeSource.Action, notice.Source);

            await scene.Type("landings 30");
            Assert.Equal(new[] { "", "30" }, scene.Landings.Skip(calls));
            Assert.Equal(dialogs + 2, scene.Dialogs.Texts.Count);
            Assert.Equal("Landings", scene.Dialogs.Texts[^1].Title);
            await scene.View.HandleKeyAsync(Key.Tab);
        }
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Empty(scene.Harness.Conductor.Calls);
    }

    [Fact]
    public async Task CommandFocusAndDecisionScopeKeepTheirExistingControls()
    {
        using var scene = new Scene();
        await scene.Render();
        await scene.View.HandleKeyAsync(new Key(':'));
        var typed = new Key('l');
        await scene.View.HandleKeyAsync(typed);
        Assert.False(typed.Handled);
        Assert.Empty(scene.Landings);
        Assert.Empty(scene.Dialogs.Texts);
        Assert.Empty(scene.View.NoticeStrip.Visible);
        await scene.View.HandleKeyAsync(Key.Esc);

        await scene.View.HandleKeyAsync(Key.Tab);
        var unavailable = new Key('a');
        await scene.View.HandleKeyAsync(unavailable);
        Assert.True(unavailable.Handled);
        var notice = Assert.Single(scene.View.NoticeStrip.Visible);
        Assert.Contains("not available here", notice.Text);
        Assert.DoesNotContain("not bound", notice.Text);
        await scene.Type("accept");
        Assert.Contains("not available here", Assert.Single(scene.View.NoticeStrip.Visible).Text);
        Assert.Empty(scene.Dialogs.Confirmations);
        Assert.Empty(scene.Harness.Answers.Calls);

        using var empty = new Scene(answerable: false);
        await empty.Render();
        await empty.View.HandleKeyAsync(new Key('a'));
        await empty.Type("accept");
        Assert.Empty(empty.View.NoticeStrip.Visible);
        Assert.Empty(empty.Dialogs.Texts);
        Assert.Empty(empty.Dialogs.Confirmations);
        Assert.Empty(empty.Harness.Answers.Calls);
    }

    [Fact]
    public async Task ViewCommandsAndKeysHaveTheSameActions()
    {
        using var scene = new Scene();
        await scene.Render();
        await scene.View.HandleKeyAsync(Key.Tab);
        foreach (var typed in new[] { false, true })
        {
            await scene.Invoke(typed, 'g', "gate");
            Assert.True(scene.View.GateView.IsOpen);
            await scene.View.HandleKeyAsync(Key.Esc);
            Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
            await scene.Invoke(typed, 'e', "epics");
            Assert.True(scene.View.EpicView.IsOpen);
            await scene.View.HandleKeyAsync(Key.Esc);
            Assert.False(scene.View.EpicView.IsOpen);
            Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
            var dialogs = scene.Dialogs.Texts.Count;
            await scene.Invoke(typed, '?', "help");
            Assert.Equal("Help", Assert.Single(scene.Dialogs.Texts.Skip(dialogs)).Title);
        }
        scene.View.FocusDecisions();
        foreach (var typed in new[] { false, true })
        {
            var confirms = scene.Dialogs.Confirmations.Count;
            await scene.Invoke(typed, 'a', "accept");
            Assert.Equal("Accept default?", Assert.Single(scene.Dialogs.Confirmations.Skip(confirms)).Title);
            var prompts = scene.Dialogs.Prompts.Count;
            await scene.Invoke(typed, 'r', "answer");
            Assert.Single(scene.Dialogs.Prompts.Skip(prompts));
        }
        Assert.Empty(scene.Harness.Answers.Calls); // Confirmation declined and answer form cancelled.
    }

    [Fact]
    public async Task EveryControllerUsageReachesTheRealControllerThroughTheView()
    {
        using var scene = new Scene();
        await scene.Render();
        foreach (var row in OwnerConsoleBindings.All.Where(row => row.Owner == OwnerConsoleCommandOwner.Controller))
        {
            var usage = row.Usage.Replace("<id-prefix>", "11111111", StringComparison.Ordinal);
            var optional = usage.IndexOf('[');
            if (optional >= 0) usage = usage[..optional].TrimEnd();
            var dialogs = scene.Dialogs.Texts.Count;
            await scene.Type(usage);
            var shown = Assert.Single(scene.Dialogs.Texts.Skip(dialogs));
            Assert.DoesNotContain("unknown command", shown.Text);
        }
        Assert.Equal(new[] { "" }, scene.Landings);
        Assert.Equal(2, scene.Harness.Conductor.Calls.Count);
        Assert.Empty(scene.Harness.Answers.Calls);
    }

    [Fact]
    public async Task DiagnosticsPreserveUnhandledAndHandledOutcomes()
    {
        var diagnostics = new OwnerConsoleInputDiagnostics();
        using var scene = new Scene(diagnostics: diagnostics);
        await scene.Render();
        await scene.View.HandleKeyAsync(new Key('x'));
        await scene.View.HandleKeyAsync(new Key('l'));
        using var stream = new MemoryStream();
        diagnostics.Export(stream);
        using var document = JsonDocument.Parse(stream.ToArray());
        var records = document.RootElement.GetProperty("Records").Deserialize<OwnerConsoleInputTraceRecord[]>()!;
        var handlers = records.Where(row => row.Phase == "handler" && row.Boundary == "end").ToArray();
        Assert.Equal(new[] { "unhandled", "handled" }, handlers.Select(row => row.Detail));
        Assert.Equal(new[] { "" }, scene.Landings);
    }

    [Fact]
    public async Task UnboundNoticesReplaceAndIgnoreModifiedOrBusyInput()
    {
        using var scene = new Scene();
        await scene.Render();
        await scene.View.HandleKeyAsync(new Key('x'));
        await scene.View.HandleKeyAsync(new Key('X'));
        Assert.StartsWith("X: not bound", Assert.Single(scene.View.NoticeStrip.Visible).Text);
        await scene.View.HandleKeyAsync(new Key('?')); // A new action clears the notice.
        Assert.Empty(scene.View.NoticeStrip.Visible);
        foreach (var key in new[] { new Key('x').WithCtrl, new Key('x').WithAlt, new Key(' '), Key.Esc })
        {
            await scene.View.HandleKeyAsync(key);
            Assert.Empty(scene.View.NoticeStrip.Visible);
        }

        scene.Dialogs.Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.Dialogs.Hold = release.Task;
        var action = scene.View.HandleKeyAsync(new Key('l'));
        try
        {
            try { await scene.Dialogs.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
            catch (TimeoutException exception) { throw new TimeoutException("Landings dialog did not open.", exception); }
            await scene.View.HandleKeyAsync(new Key('x'));
            Assert.Empty(scene.View.NoticeStrip.Visible);
            Assert.Equal(new[] { "" }, scene.Landings);
        }
        finally { release.TrySetResult(); await action; }
    }

    private sealed class Scene : IDisposable
    {
        internal OwnerConsoleHarness Harness { get; } = new();
        internal Dialogs Dialogs { get; } = new();
        internal List<string> Landings { get; } = [];
        internal OwnerConsoleFullScreenView View { get; }
        private readonly OwnerConsoleScreenController _controller;
        private readonly IApplication _app = Terminal.Gui.App.Application.Create();

        internal Scene(bool answerable = true, OwnerConsoleInputDiagnostics? diagnostics = null)
        {
            Harness.AddGoal("11111111-first", "First", AgentRole.Developer);
            if (answerable) Harness.Questions.Items.Add(new("q1", "11111111-first", OwnerQuestionKind.HumanInput, "Ship?", ProposedDefault: "ship"));
            _controller = new(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock,
                landings: argument => { Landings.Add(argument); return OwnerLandingsPage.Plain("delivered rows"); });
            View = new(_app, _controller, () => Task.CompletedTask, clock: Harness.Clock, diagnostics: diagnostics);
        }

        internal async Task Render()
        {
            View.Render(await new OwnerConsoleViewModelBuilder(Harness.State, Harness.Questions, Harness.Liveness,
                new Epics(), Harness.Clock).BuildAsync(new(Harness.Clock.GetUtcNow(), null, [], 0)));
            View.FocusDecisions();
        }

        internal async Task Type(string line)
        {
            await View.HandleKeyAsync(new Key(':'));
            View.CommandLine.Text = line;
            await View.HandleKeyAsync(Key.Enter);
        }

        internal Task Invoke(bool typed, char key, string command) => typed ? Type(":" + command) : View.HandleKeyAsync(new Key(key));
        public void Dispose() { View.Dispose(); _controller.Dispose(); _app.Dispose(); }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal List<(string Title, string Text)> Texts { get; } = [];
        internal List<(string Title, string Text)> Confirmations { get; } = [];
        internal List<(string Title, string Text)> Prompts { get; } = [];
        internal TaskCompletionSource? Opened;
        internal Task? Hold;
        public Task<bool> ConfirmAsync(string title, string text)
        { Confirmations.Add((title, text)); return Task.FromResult(false); }
        public Task<string?> PromptTextAsync(string title, string text)
        { Prompts.Add((title, text)); return Task.FromResult<string?>(null); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); Opened?.TrySetResult(); return Hold ?? Task.CompletedTask; }
    }
}
