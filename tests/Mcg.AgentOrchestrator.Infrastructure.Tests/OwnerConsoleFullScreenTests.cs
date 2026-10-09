using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: an uninitialized instance app and the key seam avoid global drivers and terminal I/O.
public sealed class OwnerConsoleFullScreenTests
{
    [Fact]
    public async Task BoardRendersTheSixHeadersAndRedrawKeepsSelectedDecisionById()
    {
        var harness = Harness();
        var controller = Controller(harness, new Dialogs());
        var refreshCalls = 0;
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => { refreshCalls++; return Task.CompletedTask; });
        view.Render(await Model(harness));
        view.FocusDecisions();
        Assert.True(view.BoardPane.Style.ShowHeaders);
        Assert.True(view.BoardPane.Style.AlwaysShowHeaders);
        Assert.Equal(["GOAL", "EPIC", "TITLE", "STATE", "STAGE", "AGE"],
            view.BoardPane.Table!.ColumnNames);
        await view.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("q2", controller.SelectedDecisionId);
        Assert.Equal(0, refreshCalls);
        harness.Questions.Items.Insert(0, new("new", "11111111", OwnerQuestionKind.HumanInput, "New decision"));

        view.Render(await Model(harness));

        Assert.Equal("q2", controller.SelectedDecisionId);
        Assert.Equal(2, controller.SelectedIndex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptKeySubmitsDefaultOnlyAfterConfirmation(bool confirmed)
    {
        var harness = Harness();
        var dialogs = new Dialogs { Confirmed = confirmed };
        var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));

        await view.HandleKeyAsync(new Key('a'));

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal("ship", dialogs.ConfirmationText);
        Assert.Equal(confirmed ? [("q1", "ship")] : Array.Empty<(string, string)>(), harness.Answers.Calls);
    }

    [Theory]
    [InlineData('a', true, "ship")]
    [InlineData('a', false, null)]
    [InlineData('r', true, "owner answer")]
    public async Task OwnerThinkTimePastBoundPreservesConfirmationAndAnswer(char key, bool confirmed, string? expected)
    {
        var harness = Harness();
        var clock = new ManualStewardTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new Dialogs
        {
            Confirmed = confirmed, Input = "owner answer", HoldResponse = release.Task,
            OnConfirm = () => entered.TrySetResult(), OnPrompt = () => entered.TrySetResult()
        };
        var controller = Controller(harness, dialogs);
        var refreshCalls = 0;
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller,
            () => { refreshCalls++; return Task.CompletedTask; }, clock: clock);
        view.Render(await Model(harness));
        var action = view.HandleKeyAsync(new Key(key));
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(OwnerConsoleLoopOptions.Default.BusyNoticeAfter);
            Assert.DoesNotContain("working:", view.StatusText);
            clock.Advance(OwnerConsoleLoopOptions.Default.OperationBound);

            Assert.False(clock.TimerCreated.Task.IsCompleted); // No dependency timer includes owner think time.
            Assert.Empty(view.Notices);
            Assert.Empty(harness.Answers.Calls);
            Assert.Equal(0, refreshCalls);
            Assert.False(action.IsCompleted);
        }
        finally { release.TrySetResult(); await action; }

        Assert.Equal(expected is null ? Array.Empty<(string, string)>() : [("q1", expected)], harness.Answers.Calls);
        Assert.Equal(key == 'a' ? 1 : 0, dialogs.ConfirmCalls);
        Assert.Equal(1, refreshCalls);
        Assert.DoesNotContain("working:", view.StatusText);
        if (expected is null)
            Assert.Empty(view.Notices);
        else
            Assert.Equal("Answer queued for question 1 (11111111); the conductor applies it on its next tick.",
                Assert.Single(view.Notices));
    }

    [Fact]
    public async Task DetailReadingPastBoundNeverStartsDependencyTimers()
    {
        var harness = Harness();
        var clock = new ManualStewardTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new Dialogs { OnText = () => entered.TrySetResult(), HoldText = release.Task };
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness, dialogs),
            () => Task.CompletedTask, clock: clock);
        view.Render(await Model(harness));
        view.FocusDecisions();
        var action = view.HandleKeyAsync(Key.Enter);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            clock.Advance(OwnerConsoleLoopOptions.Default.OperationBound);

            Assert.False(clock.TimerCreated.Task.IsCompleted);
            Assert.DoesNotContain("working:", view.StatusText);
            Assert.Empty(view.Notices);
            Assert.False(action.IsCompleted);
            Assert.Contains("Ship?\nFull context", Assert.Single(dialogs.Texts));
        }
        finally { release.TrySetResult(); await action; }
    }

    [Fact]
    public async Task CommandResultDialogRunsAfterTheBoundedDependencyCompletes()
    {
        var harness = Harness();
        var clock = new ManualStewardTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new Dialogs { OnText = () => entered.TrySetResult(), HoldText = release.Task };
        var working = new List<string?>();
        var reports = new List<string>();
        var operation = new OwnerConsoleScreenOperation(clock, working.Add, reports.Add);
        var action = Controller(harness, dialogs).RunCommandAsync("conductor status", operation,
            TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(operation.IsRunning);
            Assert.True(operation.Completion.IsCompletedSuccessfully);
            clock.Advance(OwnerConsoleLoopOptions.Default.OperationBound);

            Assert.False(action.IsCompleted);
            Assert.DoesNotContain(working, label => label is not null);
            Assert.Empty(reports);
            Assert.Contains("conductor output 1", Assert.Single(dialogs.Texts));
            Assert.Equal(string.Empty, harness.Output.Text);
        }
        finally { release.TrySetResult(); await action; }
    }

    [Fact]
    public async Task ColonConductorStatusShowsResultInDialogWithoutScrollingOutput()
    {
        var harness = Harness();
        var dialogs = new Dialogs { Input = "conductor status" };
        var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        view.FocusDecisions();

        await view.HandleKeyAsync(new Key(':'));
        view.CommandLine.Text = "conductor status";
        await view.HandleKeyAsync(Key.Enter);

        Assert.Equal(["conductor", "status"], Assert.Single(harness.Conductor.Calls));
        Assert.Contains("conductor output 1", Assert.Single(dialogs.Texts));
        Assert.Contains("conductor error 1", dialogs.Texts[0]);
        Assert.Equal(string.Empty, harness.Output.Text);
    }

    [Theory]
    [InlineData(0, "[00:05:00] digest first")]
    [InlineData(-6, "[18:05:00] digest first")]
    public async Task DigestUsesSummaryAndMetricsUsesReport(int offsetHours, string expectedFirstLine)
    {
        var harness = Harness();
        var dialogs = new Dialogs();
        var clock = new OwnerConsoleTestClock
        {
            Zone = TimeZoneInfo.CreateCustomTimeZone("metrics-test", TimeSpan.FromHours(offsetHours),
                "Metrics test", "Metrics test")
        };
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock);

        await controller.RunCommandAsync("digest");

        Assert.Equal(0, harness.DigestReport.Calls);
        Assert.Equal("Owner digest: landed=2 pending=1" + Environment.NewLine, Assert.Single(dialogs.Texts));
        await controller.RunCommandAsync("metrics");
        Assert.Equal(1, harness.DigestReport.Calls);
        Assert.Equal(new[] { expectedFirstLine, "digest second", "digest third" },
            dialogs.Texts[1].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task WorkingAndFailuresSurviveEventRedrawWithoutChangingInput()
    {
        var harness = Harness();
        var controller = Controller(harness, new Dialogs());
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        await view.HandleKeyAsync(new Key(':'));
        view.CommandLine.Text = "conductor sta";
        var focus = view.CommandLine.HasFocus;
        view.SetWorking("refresh", "refresh after conductor event");
        view.ShowRefreshFailure("conductor event read failed; retrying: unavailable");

        view.Render(await Model(harness));

        Assert.Contains("working: refresh after conductor event", view.StatusText);
        Assert.Contains("conductor event read failed", Assert.Single(view.Notices));
        Assert.Equal("conductor sta", view.CommandLine.Text);
        Assert.True(focus);
        Assert.True(view.CommandLine.HasFocus);
    }

    [Fact]
    public async Task UnsupportedCommandDependencyShowsPersistentFailure()
    {
        var harness = Harness();
        var controller = new OwnerConsoleScreenController(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, new FailingConductor(), harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness));
        await view.HandleKeyAsync(new Key(':'));
        view.CommandLine.Text = "conductor status";

        await view.HandleKeyAsync(Key.Enter);
        view.Render(await Model(harness));

        Assert.Contains("unsupported control", Assert.Single(view.Notices));
    }

    [Fact]
    public async Task NavigationDuringPendingDetailDoesNotWaitForRefresh()
    {
        var harness = Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new Dialogs { OnText = () => entered.TrySetResult(), HoldText = release.Task };
        var controller = Controller(harness, dialogs);
        var refreshCalls = 0;
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => { refreshCalls++; return Task.CompletedTask; });
        view.Render(await Model(harness));
        view.FocusDecisions();
        var detail = view.HandleKeyAsync(Key.Enter);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await view.HandleKeyAsync(Key.CursorDown);

            Assert.Equal("q2", controller.SelectedDecisionId);
            Assert.Equal(0, refreshCalls);
        }
        finally { release.TrySetResult(); await detail; }
    }

    [Fact]
    public async Task DecisionDisappearingDuringConfirmationIsNeverSubmitted()
    {
        var harness = Harness();
        var dialogs = new Dialogs { Confirmed = true, OnConfirm = () => harness.Questions.Items.Clear() };
        var controller = Controller(harness, dialogs);
        controller.Apply(await Model(harness));

        await controller.HandleKeyAsync(0, 'a');

        Assert.Empty(harness.Answers.Calls);
        Assert.Contains("no longer open", Assert.Single(dialogs.Texts));
    }

    [Theory]
    [InlineData("")]
    [InlineData("--unsafe")]
    public async Task AnswerRejectsInvalidText(string input)
    {
        var harness = Harness();
        var dialogs = new Dialogs { Input = input };
        var controller = Controller(harness, dialogs);
        controller.Apply(await Model(harness));
        await controller.HandleKeyAsync(0, 'r');
        Assert.Empty(harness.Answers.Calls);
        Assert.Contains("cannot be empty or start with --", Assert.Single(dialogs.Texts));
    }

    [Fact]
    public async Task DefaultChangingDuringConfirmationRequiresAnotherReview()
    {
        var harness = Harness();
        var dialogs = new Dialogs
        {
            Confirmed = true,
            OnConfirm = () => harness.Questions.Items[0] = harness.Questions.Items[0] with { ProposedDefault = "wait" }
        };
        var controller = Controller(harness, dialogs);
        controller.Apply(await Model(harness));
        await controller.HandleKeyAsync(0, 'a');
        Assert.Empty(harness.Answers.Calls);
        Assert.Contains("Question changed", Assert.Single(dialogs.Texts));
    }

    [Fact]
    public async Task EnterShowsFullDetailAndRedrawDoesNotOpenOrCloseDialogs()
    {
        var harness = Harness();
        var dialogs = new Dialogs();
        var controller = Controller(harness, dialogs);
        controller.Apply(await Model(harness));
        await controller.HandleKeyAsync(ConsoleKey.Enter);
        Assert.Contains("Ship?\nFull context", Assert.Single(dialogs.Texts));
        Assert.Contains("blast radius: repo", dialogs.Texts[0]);
        Assert.Contains("confidence: high", dialogs.Texts[0]);
        Assert.Contains("default: ship", dialogs.Texts[0]);
        controller.Apply(await Model(harness));
        Assert.Single(dialogs.Texts);
    }

    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Ship?\nFull context", "repo", "high", "ship"));
        harness.Questions.Items.Add(new("q2", "11111111", OwnerQuestionKind.Clarification, "Which branch?"));
        return harness;
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs) =>
        new(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail, harness.Conductor,
            harness.DigestReport, harness.Digest, harness.Clock);

    private static Task<OwnerConsoleViewModel> Model(OwnerConsoleHarness harness) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock)
            .BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class FailingConductor : IOwnerConsoleConductor
    {
        public int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
            throw new NotSupportedException("unsupported control");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal bool Confirmed;
        internal int ConfirmCalls;
        internal string? ConfirmationText;
        internal string? Input;
        internal Action? OnConfirm;
        internal Action? OnPrompt;
        internal Action? OnText;
        internal Task? HoldResponse;
        internal Task? HoldText;
        internal readonly List<string> Texts = [];
        public async Task<bool> ConfirmAsync(string title, string text)
        {
            ConfirmCalls++; ConfirmationText = text; OnConfirm?.Invoke();
            if (HoldResponse is not null) await HoldResponse;
            return Confirmed;
        }
        public async Task<string?> PromptTextAsync(string title, string text)
        {
            OnPrompt?.Invoke();
            if (HoldResponse is not null) await HoldResponse;
            return Input;
        }
        public Task ShowTextAsync(string title, string text)
        { Texts.Add(text); OnText?.Invoke(); return HoldText ?? Task.CompletedTask; }
    }
}
