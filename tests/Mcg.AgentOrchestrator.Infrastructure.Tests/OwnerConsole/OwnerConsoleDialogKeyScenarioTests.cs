using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test kernels, recording sources and uninitialized applications.
public sealed class OwnerConsoleDialogKeyScenarioTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    [Fact]
    public async Task GoalActions_QuestionFailureAndEpic_UseRegistryLabels()
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        fixture.Dialogs.OnGoal = async dialog =>
        {
            Assert.Equal(new[] { "Open question (d)", "Last failure (f)", "Epic (e)", "Gate view (g)" }, dialog.Actions);
            var closed = 0;
            dialog.CloseRequested += () => closed++;
            Assert.True(await dialog.HandleKeyAsync('d'));
            Assert.Equal("Decision", Assert.Single(fixture.Dialogs.Texts).Title);
            Assert.Contains("Choose the export format?", fixture.Dialogs.Texts[0].Text);
            Assert.True(await dialog.HandleKeyAsync('f'));
            Assert.Equal("Last failure", fixture.Dialogs.Texts[1].Title);
            Assert.Contains("SampleTests.FailsOnPurpose", fixture.Dialogs.Texts[1].Text);
            Assert.True(await dialog.HandleKeyAsync('q'));
            Assert.Equal(1, closed);
            Assert.Equal(2, fixture.Dialogs.Texts.Count);
            Assert.Null(dialog.RequestedEpicId);
            Assert.False(fixture.Controller.QuitRequested);
            Assert.True(await dialog.HandleKeyAsync('e'));
            Assert.Equal("member-epic", dialog.RequestedEpicId);
            Assert.Equal(2, closed);
        };
        await fixture.Controller.ShowGoalDetailAsync(Id);
        Assert.Single(fixture.Dialogs.Goals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoalQuit_WithOrWithoutQuestion_ClosesWithoutTextOrConsoleQuit(bool question)
    {
        using var fixture = new Fixture(question);
        await fixture.RenderAsync();
        var closed = 0;
        fixture.Dialogs.OnGoal = async dialog =>
        {
            dialog.CloseRequested += () => closed++;
            if (!question)
            {
                Assert.DoesNotContain("Open question (d)", dialog.Actions);
                Assert.False(await dialog.HandleKeyAsync('d'));
                Assert.Empty(fixture.Dialogs.Texts);
            }
            Assert.True(await dialog.HandleKeyAsync('q'));
            Assert.Equal(1, closed);
            Assert.Empty(fixture.Dialogs.Texts);
        };
        await fixture.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, fixture.View.FocusedPane);
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Single(fixture.Dialogs.Goals);
        Assert.Equal(1, closed);
        Assert.False(fixture.Controller.QuitRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpicReload_FailedAndLoaded_UsesControlAndPreservesWindow(bool detail)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        string[]? priorLines = null;
        if (detail)
        {
            await fixture.View.HandleKeyAsync(new Key('e'));
            priorLines = fixture.View.EpicView.Lines.ToArray();
            fixture.Source.Error = new InvalidOperationException("portfolio unavailable");
            await fixture.View.HandleKeyAsync(Key.Enter);
            Assert.True(fixture.View.EpicView.ShowingDetail);
        }
        else
        {
            fixture.Source.Error = new InvalidOperationException("portfolio unavailable");
            await fixture.View.HandleKeyAsync(new Key('e'));
        }
        var epic = fixture.View.EpicView;
        if (detail)
        {
            Assert.Equal(priorLines, epic.Lines);
            Assert.Contains("refresh failed: portfolio unavailable", epic.Title.ToString());
        }
        else Assert.Contains("Could not load epics: portfolio unavailable. Press Ctrl+R to retry.", epic.Lines);
        Assert.Contains("Ctrl+R retry", epic.HintText);
        var calls = fixture.Source.Calls;
        var plain = new Key('r');
        await fixture.View.HandleKeyAsync(plain);
        Assert.True(plain.Handled);
        Assert.Equal(calls, fixture.Source.Calls);
        fixture.Source.Error = null;
        await fixture.View.HandleKeyAsync(new Key('r').WithCtrl);
        Assert.Equal(calls + 1, fixture.Source.Calls);
        Assert.Equal(OwnerConsoleEpicLoadState.Loaded, epic.LoadState);
        Assert.Contains(epic.Lines, line => line.Contains("Member epic", StringComparison.Ordinal));
        if (detail)
        {
            Assert.Contains("In flight:", epic.Lines);
            Assert.Contains(epic.Lines, line => line.Contains("Member goal", StringComparison.Ordinal));
        }
        else Assert.Contains("landed 0 · in flight 1 (0 verifying, 1 active) · failed 0 · backlog 0 open / 0 done", epic.Lines);
        Assert.DoesNotContain("Ctrl+R retry", epic.HintText);
        await fixture.View.HandleKeyAsync(new Key('r').WithCtrl);
        Assert.Equal(calls + 2, fixture.Source.Calls);
        Assert.DoesNotContain("Ctrl+R retry", epic.HintText);
        Assert.Equal(detail, epic.ShowingDetail);
        Assert.Equal(OwnerConsoleEpicWindow.Day, epic.TimeWindow);
        foreach (var window in new[] { OwnerConsoleEpicWindow.Week, OwnerConsoleEpicWindow.AllTime, OwnerConsoleEpicWindow.Day })
        {
            await fixture.View.HandleKeyAsync(new Key('w'));
            Assert.Equal(window, epic.TimeWindow);
        }
        await fixture.View.HandleKeyAsync(new Key('w'));
        calls = fixture.Source.Calls;
        await fixture.View.HandleKeyAsync(new Key('R').WithCtrl);
        Assert.Equal(calls + 1, fixture.Source.Calls);
        Assert.Equal(OwnerConsoleEpicWindow.Week, epic.TimeWindow);
    }

    [Fact]
    public async Task EpicQuit_ListAndDetail_BackOutAndRestorePane()
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        await fixture.View.HandleKeyAsync(Key.Tab);
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(new Key('x'));
        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.False(fixture.Controller.QuitRequested);
        await fixture.View.HandleKeyAsync(new Key('q'));
        Assert.False(fixture.View.EpicView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, fixture.View.FocusedPane);
        Assert.False(fixture.Controller.QuitRequested);

        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.True(fixture.View.EpicView.ShowingDetail);
        await fixture.View.HandleKeyAsync(new Key('q'));
        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.False(fixture.View.EpicView.ShowingDetail);
        Assert.False(fixture.Controller.QuitRequested);
        await fixture.View.HandleKeyAsync(new Key('q'));
        Assert.False(fixture.View.EpicView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, fixture.View.FocusedPane);
        Assert.False(fixture.Controller.QuitRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateKeys_RegistryFooterAndScrolling_BackOutToActivity(bool quit)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        await fixture.View.HandleKeyAsync(Key.Tab);
        await fixture.View.HandleKeyAsync(Key.Tab);
        await fixture.View.HandleKeyAsync(new Key('g'));
        var gate = fixture.View.GateView;
        Assert.True(gate.IsOpen);
        Assert.Equal(OwnerConsoleDialogBindings.GateHelp.HintLabel, gate.FooterText);
        Assert.True(gate.Lines.Count > 1);
        await fixture.View.HandleKeyAsync(Key.End);
        Assert.Equal(gate.Lines.Count - 1, gate.SelectedLine);
        await fixture.View.HandleKeyAsync(Key.Home);
        Assert.Equal(0, gate.SelectedLine);
        await fixture.View.HandleKeyAsync(new Key('x'));
        Assert.True(gate.IsOpen);
        Assert.False(fixture.Controller.QuitRequested);
        await fixture.View.HandleKeyAsync(quit ? new Key('q') : Key.Esc);
        Assert.False(gate.IsOpen);
        Assert.Equal(OwnerConsolePane.Activity, fixture.View.FocusedPane);
        Assert.False(fixture.Controller.QuitRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateBackOut_Expanded_QuitClosesWhileEscapeCollapses(bool quit)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        await fixture.View.HandleKeyAsync(Key.Tab);
        await fixture.View.HandleKeyAsync(Key.Tab);
        await fixture.View.HandleKeyAsync(new Key('g'));
        var gate = fixture.View.GateView;
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.True(gate.IsOpen);
        Assert.True(gate.Expanded);
        await fixture.View.HandleKeyAsync(new Key('x'));
        Assert.True(gate.IsOpen);
        Assert.True(gate.Expanded);
        Assert.False(fixture.Controller.QuitRequested);

        await fixture.View.HandleKeyAsync(quit ? new Key('q') : Key.Esc);
        Assert.Equal(!quit, gate.IsOpen);
        Assert.False(fixture.Controller.QuitRequested);
        if (!quit)
        {
            Assert.False(gate.Expanded);
            await fixture.View.HandleKeyAsync(Key.Esc);
        }
        Assert.False(gate.IsOpen);
        Assert.Equal(OwnerConsolePane.Activity, fixture.View.FocusedPane);
        Assert.False(fixture.Controller.QuitRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TopLevelQuit_EachPane_StillQuits(int pane)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        for (var index = 0; index < pane; index++) await fixture.View.HandleKeyAsync(Key.Tab);
        Assert.Equal((OwnerConsolePane)pane, fixture.View.FocusedPane);
        await fixture.View.HandleKeyAsync(new Key('q'));
        Assert.True(fixture.Controller.QuitRequested);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly Dialogs Dialogs = new();
        internal readonly Source Source;
        internal readonly IApplication App = Terminal.Gui.App.Application.Create();
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;

        internal Fixture(bool question = true)
        {
            Harness.AddGoal(Id, "Member goal", AgentRole.Developer);
            var snapshot = Harness.Kernel.ExportSnapshot();
            Harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
            {
                LatestAcceptanceFailure = new(At, ["SampleTests.FailsOnPurpose"], CheckAttributions:
                    [new("SampleTests.FailsOnPurpose", AcceptanceFailureOrigin.Introduced, "first failure message")])
            }).ToArray() });
            if (question) Harness.Questions.Items.Add(new("question", Id, OwnerQuestionKind.HumanInput, "Choose the export format?"));
            Source = new(Harness.State);
            Controller = OwnerConsoleControllerFactory.Create(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock, Source);
            View = new(App, Controller, () => Task.CompletedTask);
        }

        internal async Task RenderAsync()
        {
            var model = await new OwnerConsoleViewModelBuilder(Harness.State, Harness.Questions, Harness.Liveness, new Lookup(), Harness.Clock)
                .BuildAsync(new(Harness.Clock.GetUtcNow(), null, [], 0));
            var gate = new OwnerConsoleGateProgress(true, "solo", [new(Id, "Member goal")], At, TimeSpan.Zero,
                [new("Running", OwnerConsoleGateLaneState.Running, "unknown", "local", TimeSpan.FromSeconds(2), 10, false),
                 new("Queued", OwnerConsoleGateLaneState.Queued, "unknown", "local", TimeSpan.Zero, 10, false)], 0, 20, 0, 2);
            View.Render(model with { Gate = gate });
            View.FocusDecisions();
        }

        public void Dispose() { View.Dispose(); App.Dispose(); }
    }

    private sealed class Source(IOrchestratorStateQueries state) : IOwnerConsoleEpicSource
    {
        internal int Calls;
        internal Exception? Error;
        private static IReadOnlyList<EpicProgressRollup> Rows(DateTimeOffset? since) => EpicProgressReadModel.Build(
            [new("member-epic", "Member epic", null, At, "test", At, "test")], [],
            [new("member-epic", PortfolioMemberKind.Goal, Id, At, "test")],
            [new(Id, GoalStatus.Active.ToString(), "Member goal", At.ToString("O"))], [], since);

        public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token)
        {
            Calls++;
            return Error is null ? Task.FromResult(Rows(since)) : Task.FromException<IReadOnlyList<EpicProgressRollup>>(Error);
        }

        public async Task<EpicDetailReadSnapshot> LoadDetailAsync(string epicId, DateTimeOffset? since, CancellationToken token)
        {
            var row = (await LoadAsync(since, token)).Single(row => row.Epic.Id == epicId);
            var goals = (await state.LoadGoalsAsync([new GoalId(Id)], token)).Goals.ToDictionary(goal => goal.Id.Value);
            return new(row with { Plan = new(new(epicId, null, [], []), [], []) }, since, goals);
        }

        public Task<string?> FindGoalEpicAsync(string goalId, CancellationToken token) => Task.FromResult<string?>(goalId == Id ? "member-epic" : null);
    }

    private sealed class Lookup : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        internal readonly List<OwnerConsoleGoalDialog> Goals = [];
        internal Func<OwnerConsoleGoalDialog, Task>? OnGoal;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
        public Task ShowTextAsync(string title, string text) { Texts.Add((title, text)); return Task.CompletedTask; }
        public async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
        { Goals.Add(dialog); dialog.Start(action => action()); await dialog.LastLoad; if (OnGoal is not null) await OnGoal(dialog); }
    }
}
