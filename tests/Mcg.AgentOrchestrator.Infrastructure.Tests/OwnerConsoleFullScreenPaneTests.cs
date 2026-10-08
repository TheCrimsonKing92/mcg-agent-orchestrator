using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: instance application without terminal initialization or global drivers.
public sealed class OwnerConsoleFullScreenPaneTests
{
    [Fact]
    public async Task BoardArrowsAndEnterOpenSelectedGoalsSharedDetail()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First goal", AgentRole.Developer);
        var selected = harness.AddGoal("22222222-second", "Second goal", AgentRole.Tester);
        var tail = new Tail();
        var dialogs = new Dialogs();
        var controller = Controller(harness, dialogs, tail);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));

        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);
        await view.HandleKeyAsync(Key.Enter);

        var detail = Assert.Single(dialogs.Texts);
        Assert.Equal("Goal", detail.Title);
        Assert.Equal(selected.Id.Value, view.SelectedGoalId);
        Assert.Contains(selected.Id.Value, detail.Text);
        Assert.Contains("Second goal", detail.Text);
        Assert.Contains(selected.Status.ToString(), detail.Text);
        Assert.Contains("Stage: Created", detail.Text);
        Assert.Contains("Role: Tester", detail.Text);
        Assert.Contains("Recent events: none", detail.Text);
        Assert.DoesNotContain("newest event for " + selected.Id.Value, detail.Text);
        Assert.Equal((selected.Id.Value, 10), Assert.Single(tail.Calls));

        await controller.RunCommandAsync(":goal " + selected.Id.Value);
        Assert.Contains("newest event for " + selected.Id.Value, dialogs.Texts[1].Text);
        Assert.Equal((selected.Id.Value, 15), tail.Calls[1]);
        await view.HandleKeyAsync(Key.CursorUp);
        Assert.Equal("11111111-first", view.SelectedGoalId);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task BoardSelectionSurvivesReorderingAndClampsAfterRemoval()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First", AgentRole.Developer);
        harness.AddGoal("22222222-second", "Second", AgentRole.Developer);
        var dialogs = new Dialogs();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness, dialogs), () => Task.CompletedTask);
        var model = await Model(harness);
        view.Render(model);
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);

        view.Render(model with { Board = [model.Board[1], model.Board[0]] });
        Assert.Equal("22222222-second", view.SelectedGoalId);
        Assert.Equal(0, view.BoardPane.Value?.SelectedCell.Y);
        view.Render(model with { Board = [model.Board[0]] });
        Assert.Equal("11111111-first", view.SelectedGoalId);
        view.Render(model with { Board = [] });
        Assert.Null(view.SelectedGoalId);
        await view.HandleKeyAsync(Key.Enter);
        Assert.Empty(dialogs.Texts);
    }

    [Fact]
    public async Task EmptyDecisionsAreNonSelectableAndActionKeysChangeNothing()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First", AgentRole.Developer);
        var dialogs = new Dialogs();
        var controller = Controller(harness, dialogs);
        var refreshes = 0;
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller,
            () => { refreshes++; return Task.CompletedTask; });
        view.Render(await Model(harness));
        view.FocusDecisions();
        Assert.Equal(["Nothing needs you right now."], view.DecisionLines);
        Assert.Equal(0, view.DecisionsPane.Source!.Count); // The placeholder is text, never a list row.
        Assert.Null(view.DecisionsPane.SelectedItem);
        var status = view.StatusText;
        var board = view.SelectedGoalId;
        var hints = view.HintText;
        var calls = harness.State.Calls.ToArray();

        foreach (var key in new[] { Key.Enter, new Key('a'), new Key('r') })
        {
            await view.HandleKeyAsync(key);
            Assert.Equal(["Nothing needs you right now."], view.DecisionLines);
            Assert.Null(controller.SelectedDecisionId);
            Assert.Null(view.DecisionsPane.SelectedItem);
            Assert.Equal(status, view.StatusText);
            Assert.Equal(board, view.SelectedGoalId);
            Assert.Equal(hints, view.HintText);
            Assert.Equal(calls, harness.State.Calls);
            Assert.Empty(view.Notices);
            Assert.Empty(dialogs.Texts);
            Assert.Equal(0, dialogs.InputCalls);
            Assert.Empty(harness.Answers.Calls);
            Assert.Equal(0, refreshes);
        }
    }

    [Fact]
    public async Task TabCyclesPaneHintsAndHelpListsAllBindingsAndCommands()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First", AgentRole.Developer);
        var dialogs = new Dialogs();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness, dialogs), () => Task.CompletedTask);
        view.Render(await Model(harness));
        const string common = "  Tab next pane  : command  ? help  q quit";
        Assert.Equal("Enter detail  a accept default  r answer" + common, view.HintText);
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
        Assert.Equal("Enter goal detail" + common, view.HintText);
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Activity, view.FocusedPane);
        Assert.Equal("Up/Down scroll" + common, view.HintText);
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Decisions, view.FocusedPane);
        Assert.Equal("Enter detail  a accept default  r answer" + common, view.HintText);

        await view.HandleKeyAsync(new Key('?'));

        var help = Assert.Single(dialogs.Texts);
        Assert.Equal("Help", help.Title);
        foreach (var binding in new[] { "Tab:", "Up:", "Down:", "Enter:", "a:", "r:", ": (any pane):", "?:", "q:", "Esc:" })
            Assert.Contains(binding, help.Text);
        foreach (var command in new[] { "conductor start", "conductor stop", "conductor status", "digest", "metrics", "bell on", "bell off", "goal <id-prefix>" })
            Assert.Contains(help.Text.Split('\n'), line => line.StartsWith(":" + command + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivityArrowsScrollAndDecisionActionsStayInDecisions()
    {
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Ship?", ProposedDefault: "ship"));
        var dialogs = new Dialogs();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness, dialogs), () => Task.CompletedTask);
        view.Render(await Model(harness));
        view.ShowRefreshFailure("older notice");
        view.ShowRefreshFailure("newer notice");
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(new Key('a'));
        await view.HandleKeyAsync(new Key('r'));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(1, view.ActivityPane.SelectedItem);
        await view.HandleKeyAsync(Key.CursorUp);
        Assert.Equal(0, view.ActivityPane.SelectedItem);
        await view.HandleKeyAsync(Key.Enter);
        await view.HandleKeyAsync(new Key('a'));
        await view.HandleKeyAsync(new Key('r'));
        Assert.Empty(dialogs.Texts);
        Assert.Equal(0, dialogs.InputCalls);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task OpenBoardDialogKeepsItsSnapshotAndDoesNotStackHelp()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First", AgentRole.Developer);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new Dialogs { Opened = opened, Hold = release.Task };
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness, dialogs), () => Task.CompletedTask);
        var model = await Model(harness);
        view.Render(model);
        await view.HandleKeyAsync(Key.Tab);
        var action = view.HandleKeyAsync(Key.Enter);
        try
        {
            await opened.Task.WaitAsync(TestContext.Current.CancellationToken);
            var text = Assert.Single(dialogs.Texts).Text;
            view.Render(model with { Board = [] });
            foreach (var key in new[] { Key.Tab, Key.CursorDown, new Key('?'), new Key('a'), new Key('r') })
                await view.HandleKeyAsync(key);
            Assert.Equal(text, Assert.Single(dialogs.Texts).Text);
            Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
            Assert.False(action.IsCompleted);
        }
        finally { release.TrySetResult(); await action; }
    }

    [Fact]
    public async Task EveryHelpCommandReachesControllerDispatch()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111-first", "First", AgentRole.Developer);
        var dialogs = new Dialogs();
        var controller = Controller(harness, dialogs);
        foreach (var item in OwnerConsoleKeyHints.Commands)
        {
            await controller.RunCommandAsync(item.Command.Replace("<id-prefix>", "11111111", StringComparison.Ordinal));
            Assert.DoesNotContain("unknown command", dialogs.Texts[^1].Text);
            Assert.DoesNotContain("usage:", dialogs.Texts[^1].Text);
        }
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs, IGoalEventTail? tail = null) =>
        new(harness.Questions, harness.Answers, dialogs, harness.State, tail ?? harness.Tail, harness.Conductor,
            harness.DigestReport, harness.Digest, harness.Clock);

    private static Task<OwnerConsoleViewModel> Model(OwnerConsoleHarness harness) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock)
            .BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Tail : IGoalEventTail
    {
        internal readonly List<(string Id, int Count)> Calls = [];
        public IReadOnlyList<string> ReadLast(string goalId, int count)
        { Calls.Add((goalId, count)); return ["older event", "newest event for " + goalId]; }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        internal int InputCalls;
        internal TaskCompletionSource? Opened;
        internal Task? Hold;
        public Task<bool> ConfirmAsync(string title, string text) { InputCalls++; return Task.FromResult(false); }
        public Task<string?> PromptTextAsync(string title, string text) { InputCalls++; return Task.FromResult<string?>(null); }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); Opened?.TrySetResult(); return Hold ?? Task.CompletedTask; }
    }
}
