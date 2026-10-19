using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: instance application, private state and recording seams; no terminal driver.
public sealed class OwnerConsoleCommandCompletionScenarioTests
{
    [Fact]
    public async Task Tab_DiscoverySequence_CompletesWithoutCallsUntilEnter()
    {
        using var scene = new Scene();
        await scene.Render();
        await scene.OpenCommand();
        await scene.Complete("go");
        Assert.Equal("goal ", scene.View.CommandLine.Text);
        Assert.Equal(scene.View.CommandLine.Text.Length, scene.View.CommandLine.InsertionPoint);
        Assert.Contains("goal <id-prefix>: Open live goal detail", scene.View.HintText);
        scene.AssertNoCalls();

        await scene.Complete("conductor st");
        Assert.Equal("conductor st", scene.View.CommandLine.Text);
        Assert.StartsWith("matches (3): ", scene.View.HintText);
        foreach (var usage in new[] { "conductor start", "conductor stop", "conductor status" })
            Assert.Contains(usage, scene.View.HintText);
        scene.AssertNoCalls();

        await scene.Complete("conductor sta");
        Assert.Equal("conductor sta", scene.View.CommandLine.Text);
        Assert.StartsWith("matches (2): ", scene.View.HintText);
        Assert.DoesNotContain("conductor stop", scene.View.HintText);
        scene.AssertNoCalls();

        await scene.Complete("conductor stat");
        Assert.Equal("conductor status", scene.View.CommandLine.Text);
        scene.AssertNoCalls();
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Equal(new[] { "conductor", "status" }, Assert.Single(scene.Harness.Conductor.Calls));
        Assert.Single(scene.Dialogs.Texts);
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Empty(scene.Dialogs.Confirmations);
        Assert.Empty(scene.Dialogs.Prompts);
    }

    [Fact]
    public async Task Tab_ZeroManyEditAndCancel_RestoresPaneAndEditingHints()
    {
        using var scene = new Scene();
        await scene.Render();
        var paneHint = scene.View.HintText;
        foreach (var input in new[] { "frob", "", ":la" })
        {
            await scene.OpenCommand();
            await scene.Complete(input);
            if (input == "frob")
            {
                Assert.Equal(input, scene.View.CommandLine.Text);
                Assert.StartsWith("no command starts with \"frob\"; commands: ", scene.View.HintText);
            }
            else if (input.Length == 0)
            {
                Assert.Equal("", scene.View.CommandLine.Text);
                Assert.StartsWith($"matches ({OwnerConsoleBindings.Commands.Count()}): ", scene.View.HintText);
            }
            else Assert.Equal(":landings", scene.View.CommandLine.Text.TrimEnd());
            scene.AssertNoCalls();
            await scene.View.HandleKeyAsync(Key.Esc);
            Assert.Equal("", scene.View.CommandLine.Text);
            Assert.Equal(paneHint, scene.View.HintText);
            Assert.Equal(OwnerConsolePane.Decisions, scene.View.FocusedPane);
            scene.AssertNoCalls();
        }
        await scene.OpenCommand();
        await scene.Complete("go");
        scene.View.CommandLine.Text = "different";
        Assert.Equal(OwnerConsoleKeyHints.CommandLineHint, scene.View.HintText);
        scene.AssertNoCalls();
    }

    [Fact]
    public async Task Enter_UnknownCommandAfterCompletion_EqualsUntouchedScene()
    {
        using var completed = new Scene();
        using var baseline = new Scene();
        await completed.Render();
        await baseline.Render();
        await completed.OpenCommand();
        await completed.Complete("frob");
        await baseline.OpenCommand();
        foreach (var scene in new[] { completed, baseline })
        {
            scene.View.CommandLine.Text = "frobnicate";
            await scene.View.HandleKeyAsync(Key.Enter);
        }
        Assert.Single(completed.Dialogs.Texts);
        Assert.Contains("unknown command", completed.Dialogs.Texts[0].Text);
        AssertEquivalent(baseline, completed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnboundKey_AfterCompletionCancel_EqualsUntouchedPane(int paneIndex)
    {
        var pane = (OwnerConsolePane)paneIndex;
        using var completed = new Scene();
        using var baseline = new Scene();
        foreach (var scene in new[] { completed, baseline })
        {
            await scene.Render();
            for (var index = 0; index < (int)pane; index++) await scene.View.HandleKeyAsync(Key.Tab);
            Assert.Equal(pane, scene.View.FocusedPane);
        }
        await completed.OpenCommand();
        await completed.Complete("");
        await completed.View.HandleKeyAsync(Key.Esc);
        foreach (var scene in new[] { completed, baseline }) await scene.View.HandleKeyAsync(new Key('x'));
        AssertEquivalent(baseline, completed);
        completed.AssertNoCalls();
    }

    [Fact]
    public async Task ModifiedTab_WhileEditing_LeavesCompletionUntouched()
    {
        using var scene = new Scene();
        await scene.Render();
        await scene.OpenCommand();
        scene.View.CommandLine.Text = "go";
        foreach (var key in new[] { Key.Tab.WithShift, Key.Tab.WithCtrl, Key.CursorLeft })
        {
            await scene.View.HandleKeyAsync(key);
            Assert.False(key.Handled);
            Assert.Equal("go", scene.View.CommandLine.Text);
            Assert.Equal(OwnerConsoleKeyHints.CommandLineHint, scene.View.HintText);
        }
        scene.AssertNoCalls();
    }

    private static void AssertEquivalent(Scene expected, Scene actual)
    {
        Assert.Equal(expected.Dialogs.Texts, actual.Dialogs.Texts);
        Assert.Equal(expected.Dialogs.Confirmations, actual.Dialogs.Confirmations);
        Assert.Equal(expected.Dialogs.Prompts, actual.Dialogs.Prompts);
        Assert.Equal(expected.Harness.Answers.Calls, actual.Harness.Answers.Calls);
        Assert.Equal(expected.Harness.Conductor.Calls.Count, actual.Harness.Conductor.Calls.Count);
        for (var index = 0; index < expected.Harness.Conductor.Calls.Count; index++)
            Assert.Equal(expected.Harness.Conductor.Calls[index], actual.Harness.Conductor.Calls[index]);
        Assert.Equal(expected.View.NoticeText, actual.View.NoticeText);
        Assert.Equal(expected.View.HintText, actual.View.HintText);
    }

    private sealed class Scene : IDisposable
    {
        internal OwnerConsoleHarness Harness { get; } = new();
        internal Dialogs Dialogs { get; } = new();
        internal OwnerConsoleFullScreenView View { get; }
        private readonly OwnerConsoleScreenController _controller;
        private readonly IApplication _app = Terminal.Gui.App.Application.Create();

        internal Scene()
        {
            Harness.AddGoal("11111111-first", "First", AgentRole.Developer);
            Harness.Questions.Items.Add(new("q1", "11111111-first", OwnerQuestionKind.HumanInput, "Ship?", ProposedDefault: "ship"));
            _controller = new(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock);
            View = new(_app, _controller, () => Task.CompletedTask, clock: Harness.Clock);
        }

        internal async Task Render()
        {
            View.Render(await new OwnerConsoleViewModelBuilder(Harness.State, Harness.Questions, Harness.Liveness,
                new Epics(), Harness.Clock).BuildAsync(new(Harness.Clock.GetUtcNow(), null, [], 0)));
            View.FocusDecisions();
        }

        internal Task OpenCommand() => View.HandleKeyAsync(new Key(':'));
        internal async Task Complete(string text)
        {
            View.CommandLine.Text = text;
            var tab = Key.Tab;
            await View.HandleKeyAsync(tab);
            Assert.True(tab.Handled);
        }

        internal void AssertNoCalls()
        {
            Assert.Empty(Harness.Conductor.Calls);
            Assert.Empty(Harness.Answers.Calls);
            Assert.Empty(Dialogs.Texts);
            Assert.Empty(Dialogs.Confirmations);
            Assert.Empty(Dialogs.Prompts);
        }

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
        public Task<bool> ConfirmAsync(string title, string text)
        { Confirmations.Add((title, text)); return Task.FromResult(false); }
        public Task<string?> PromptTextAsync(string title, string text)
        { Prompts.Add((title, text)); return Task.FromResult<string?>(null); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }
}
