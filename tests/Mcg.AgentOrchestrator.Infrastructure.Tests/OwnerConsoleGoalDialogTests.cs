using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test kernels and dialogs, no initialized terminal or external stores.
public sealed class OwnerConsoleGoalDialogTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BoardEnterShowsSecondGatePositionAndShortHeader()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111111111111111111111111111", "First", AgentRole.Developer);
        harness.AddGoal(Id, "Second goal", AgentRole.Developer);
        harness.AddGoal("33333333333333333333333333333333", "Third", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select((goal, index) => goal with
        {
            Status = GoalStatus.Verified,
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verified", At.AddMinutes(index))]
        }).ToArray() });
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.CursorDown);
        await view.HandleKeyAsync(Key.Enter);

        var detail = Assert.Single(dialogs.Goals);
        Assert.StartsWith("22222222  Second goal" + Environment.NewLine, detail.Current.Text);
        Assert.DoesNotContain(Id, detail.Current.Text);
        Assert.Contains("Landing: waiting for the gate (2nd)", detail.Current.Text);
    }

    [Fact]
    public async Task RecordedGateFailureShowsCheckAndFirstMessageLines()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Failed goal", AgentRole.Developer);
        Change(harness, goal => goal with
        {
            Status = GoalStatus.AcceptanceFailed,
            LatestAcceptanceFailure = new(At, ["SampleTests.FailsOnPurpose"], CheckAttributions:
                [new("SampleTests.FailsOnPurpose", AcceptanceFailureOrigin.Introduced, "first message\nsecond message\nthird message\nfourth message")])
        });
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        dialogs.OnGoal = async dialog =>
        {
            Assert.Contains("Landing: last gate failed", dialog.Current.Text);
            Assert.Contains("SampleTests.FailsOnPurpose", dialog.Current.Text);
            Assert.Contains("Last failure (f)", dialog.Actions);
            Assert.True(await dialog.HandleKeyAsync('F'));
        };
        await controller.ShowGoalDetailAsync(Id);
        var failure = Assert.Single(dialogs.Texts);
        Assert.Equal("Last failure", failure.Title);
        Assert.Contains("SampleTests.FailsOnPurpose", failure.Text);
        Assert.Contains("first message\nsecond message\nthird message".Replace("\n", Environment.NewLine), failure.Text);
        Assert.DoesNotContain("fourth message", failure.Text);
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
    }

    [Theory]
    [InlineData(true, 'q')]
    [InlineData(true, 'Q')]
    [InlineData(false, 'q')]
    public async Task QuestionActionOnlyOpensAnOfferedDecision(bool hasQuestion, char key)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Question goal", AgentRole.Developer);
        if (hasQuestion) harness.Questions.Items.Add(new("question", Id, OwnerQuestionKind.HumanInput, "Choose the export format?"));
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        controller.Apply(await Model(harness));
        dialogs.OnGoal = async dialog =>
        {
            Assert.Equal(hasQuestion, dialog.Actions.Contains("Open question (q)"));
            Assert.Equal(hasQuestion, await dialog.HandleKeyAsync(key));
        };
        await controller.ShowGoalDetailAsync(Id);
        if (hasQuestion)
        {
            var decision = Assert.Single(dialogs.Texts);
            Assert.Equal("Decision", decision.Title);
            Assert.Contains("Choose the export format?", decision.Text);
        }
        else Assert.Empty(dialogs.Texts);
        Assert.False(controller.QuitRequested);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task ModelRefreshUpdatesStageAndActionsWithoutMovingScroll()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId(Id), "Live goal", Enumerable.Range(0, 25)
            .Select(index => new TaskSpec(TaskId.New(), "Task " + index, AgentRole.Developer)).ToArray());
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        dialogs.OnGoal = async dialog =>
        {
            Assert.Contains("Stage: Created", dialog.Current.Text);
            dialog.Page.Resize(60, 5);
            Assert.True(dialog.Page.HandleKey(Key.PageDown));
            var offset = dialog.Page.Offset;
            Assert.True(offset > 0);
            Change(harness, snapshot => snapshot with { Tasks = snapshot.Tasks.Select((task, index) =>
                index == 0 ? task with { Status = WorkTaskStatus.Running } : task).ToArray() });
            view.Render(await Model(harness));
            await dialog.LastLoad.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Stage: Dispatched", dialog.Current.Text);
            Assert.DoesNotContain("Stage: Created", dialog.Current.Text);
            Assert.Equal(offset, dialog.Page.Offset);
            Change(harness, snapshot => snapshot with { Status = GoalStatus.Verified });
            harness.Questions.Items.Add(new("new-question", Id, OwnerQuestionKind.HumanInput, "Continue?"));
            view.Render(await Model(harness));
            await dialog.LastLoad.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Status: Verified", dialog.Current.Text);
            Assert.Contains("Landing: waiting for the gate (1st)", dialog.Current.Text);
            Assert.Equal(offset, dialog.Page.Offset);
            Assert.Contains("Open question (q)", dialog.Actions);
            Assert.True(await dialog.HandleKeyAsync('q'));
        };
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        Assert.Contains("Continue?", Assert.Single(dialogs.Texts).Text);
    }

    [Fact]
    public async Task PendingRefreshLeavesActionsResponsiveAndDropsContentAfterClose()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Responsive goal", AgentRole.Developer);
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<OwnerConsoleGoalDialog.Content?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var question = new OwnerConsoleDecision("question", 1, Id, "22222222", OwnerQuestionKind.HumanInput,
            "Continue?", "Continue while refreshing?", null, null, null);
        var content = new OwnerConsoleGoalDialog.Content("Original content", [], [], question, null, null);
        using var dialog = new OwnerConsoleGoalDialog(controller, content,
            _ => { entered.SetResult(); return pending.Task; }, controller.ShowDecisionAsync, dialogs.ShowTextAsync,
            TestContext.Current.CancellationToken);
        dialog.Start(action => action());
        var model = await Model(harness);
        controller.Apply(model);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        // The loader is held by pending, so this action completes independently of refresh.
        Assert.True(await dialog.HandleKeyAsync('q'));
        Assert.Contains("Continue while refreshing?", Assert.Single(dialogs.Texts).Text);
        dialog.Dispose();
        pending.SetResult(content with { Text = "Late content" });
        await dialog.LastLoad.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Original content", dialog.Current.Text);
        Assert.DoesNotContain("Late content", dialog.Page.Lines);
    }

    [Fact]
    public async Task PrefixCommandOpensIdenticalBoardDetailWithoutRawEvents()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Same goal", AgentRole.Developer);
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        await controller.RunCommandAsync(":goal 22222222");
        Assert.Equal(2, dialogs.Goals.Count);
        Assert.Equal(dialogs.Goals[0].Current.Text, dialogs.Goals[1].Current.Text);
        Assert.All(dialogs.Goals, dialog => Assert.DoesNotContain("recent event", dialog.Current.Text));
        Assert.Empty(dialogs.Texts);
    }

    [Fact]
    public async Task AmbiguousPrefixListsCandidatesAndUnknownPrefixSaysSo()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "First match", AgentRole.Developer);
        harness.AddGoal("22222223aaaaaaaaaaaaaaaaaaaaaaaa", "Second match", AgentRole.Developer);
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);
        await controller.RunCommandAsync(":goal 2222222");
        Assert.Contains("22222222  First match", dialogs.Texts[0].Text);
        Assert.Contains("22222223  Second match", dialogs.Texts[0].Text);
        await controller.RunCommandAsync(":goal missing");
        Assert.Contains("no goal matches 'missing'", dialogs.Texts[1].Text);
        await controller.RunCommandAsync(":goal");
        Assert.Contains("usage: goal <id-prefix>", dialogs.Texts[2].Text);
        Assert.Empty(dialogs.Goals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpicActionOpensExistingViewForTheGoalsEpicWithoutMutation(bool viaCommand)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member goal", AgentRole.Developer);
        var before = harness.Kernel.ExportSnapshot();
        var dialogs = new Dialogs { OnGoal = async dialog =>
        {
            Assert.Contains("Epic (e)", dialog.Actions);
            Assert.True(await dialog.HandleKeyAsync('e'));
        } };
        using var controller = Controller(harness, dialogs, new EpicSource());
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        await view.HandleKeyAsync(Key.Tab);
        if (viaCommand)
        {
            await view.HandleKeyAsync(new Key(':'));
            view.CommandLine.Text = ":goal 22222222";
        }
        await view.HandleKeyAsync(Key.Enter);
        Assert.True(view.EpicView.IsOpen);
        Assert.True(view.EpicView.ShowingDetail);
        Assert.Equal("member-epic", view.EpicView.SelectedEpicId);
        Assert.Contains(view.EpicView.Lines, line => line.Contains("Member epic", StringComparison.Ordinal));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before),
            System.Text.Json.JsonSerializer.Serialize(harness.Kernel.ExportSnapshot()));
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
    }

    private static void Change(OwnerConsoleHarness harness, Func<GoalSnapshot, GoalSnapshot> change)
    {
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(change).ToArray() });
    }
    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs, IOwnerConsoleEpicSource? epics = null) =>
        new(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail, harness.Conductor,
            harness.DigestReport, harness.Digest, harness.Clock, epics);
    private static Task<OwnerConsoleViewModel> Model(OwnerConsoleHarness harness) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Lookup(), harness.Clock)
            .BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
    private sealed class Lookup : IOwnerGoalEpicLookup
    { public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(""); }
    private sealed class EpicSource : IOwnerConsoleEpicSource
    {
        public Task<EpicPlanView> LoadPlanAsync(PortfolioEpic epic, CancellationToken token) =>
            Task.FromResult(new EpicPlanView(new(epic.Id, null, [], []), [], []));
        public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<EpicProgressRollup>>([new(new("member-epic", "Member epic", null, At, "test", At, "test"), null,
                [], [new(Id, EpicProgressBucket.Active, "Active", At, "Member goal")], 1, 0, 1, 0, 0, 0, At, 0, 0, 0, 0, 0, 0)]);
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<OwnerConsoleGoalDialog> Goals = [];
        internal readonly List<(string Title, string Text)> Texts = [];
        internal Func<OwnerConsoleGoalDialog, Task>? OnGoal;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Read-only goal action asked for confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Read-only goal action asked for input");
        public Task ShowTextAsync(string title, string text) { Texts.Add((title, text)); return Task.CompletedTask; }
        public async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
        { Goals.Add(dialog); dialog.Start(action => action()); if (OnGoal is not null) await OnGoal(dialog); }
    }
}
