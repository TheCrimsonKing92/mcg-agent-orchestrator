using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: per-test kernels, fixed gates and uninitialized application instances.
public sealed class OwnerConsoleGoalGateJumpTests
{
    private const string Id = "22222222222222222222222222222222";
    private const string GateHelp = "Goal detail g: Open the GATE view on this goal's gate (same as :gate <goal-id-prefix>).";

    [Fact]
    public void Registry_GoalGateAlias_PreservesNounPromptAndHelpOrder()
    {
        Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));
        var row = OwnerConsoleBindings.ForDialogKey(OwnerConsoleScope.GoalDetail, 'G');
        Assert.NotNull(row);
        Assert.Equal("gate", row.Command);
        Assert.Equal(OwnerConsoleBindings.Gate.Noun, row.Noun);
        Assert.Equal("Gate view (g)", OwnerConsoleDialogBindings.Label(row));
        Assert.Same(OwnerConsoleBindings.Gate, OwnerConsoleBindings.ForLine(":gate 457de2e6"));
        Assert.Equal("conductor start | conductor stop | conductor status | digest | metrics | bell on | bell off | goal <id-prefix> | [e] epics | fleet | scope [<name>[,<name>...]|all] | follow | [/] filter <text> | [l] landings | [g] gate | [?] help | [q] quit | [a] accept | [r] answer",
            OwnerConsoleBindings.CommandPrompt);
        var lines = OwnerConsoleBindings.HelpText.Split('\n');
        var epic = Array.IndexOf(lines, "Goal detail e: Open the goal's epic.");
        Assert.True(epic >= 0);
        Assert.Equal(GateHelp, lines[epic + 1]);
        Assert.Equal("Goal detail q: Back out of goal detail.", lines[epic + 2]);
        Assert.Contains("gate <goal-id-prefix>", Assert.Single(lines.Where(line => line.StartsWith(":gate:", StringComparison.Ordinal))));
        var violation = Assert.Single(OwnerConsoleBindings.Validate([OwnerConsoleBindings.Gate, row with { Noun = "goal" }]));
        Assert.Contains("'g'", violation);
    }

    [Theory]
    [InlineData(false, 'g')]
    [InlineData(true, 'G')]
    public async Task GoalDetail_MemberGate_JumpsPinsRefreshesAndMatchesTypedCommand(bool secondary, char key)
    {
        var harness = Harness();
        var before = JsonSerializer.Serialize(harness.Kernel.ExportSnapshot());
        var member = MemberGate();
        var gate = secondary ? OtherGate() with { OtherLiveGates = [member] } : member;
        var goalOpened = false;
        var dialogs = new Dialogs { OnGoal = async dialog =>
        {
            goalOpened = true;
            Assert.Contains("Gate view (g)", dialog.Actions);
            Assert.Contains("Gate: train, 2 members", dialog.Current.Text);
            var closed = false;
            dialog.CloseRequested += () => closed = true;
            Assert.True(await dialog.HandleKeyAsync(key));
            Assert.True(closed);
            Assert.Equal(Id, dialog.RequestedGateGoalId);
        } };
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness, gate));
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(Id, view.SelectedGoalId);

        await view.HandleKeyAsync(Key.Enter);

        Assert.True(goalOpened);
        Assert.True(view.GateView.IsOpen);
        Assert.Equal(new[] { "  22222222  Member", "  33333333  Companion" }, view.GateView.Lines.Skip(1).Take(2));
        var jumped = view.GateView.Lines.ToArray();
        Assert.Null(controller.TakeRequestedGateGoalId());
        view.Render(await Model(harness, gate));
        Assert.Equal(jumped, view.GateView.Lines);
        await view.HandleKeyAsync(Key.Enter); // Expansion also re-renders the pinned gate.
        Assert.Equal(new[] { "  22222222  Member", "  33333333  Companion" }, view.GateView.Lines.Skip(1).Take(2));
        await view.HandleKeyAsync(new Key('q'));
        await Command(view, ":gate " + Id[..8]);
        Assert.True(view.GateView.IsOpen);
        Assert.Equal(jumped, view.GateView.Lines);

        await view.HandleKeyAsync(new Key('q'));
        await view.HandleKeyAsync(new Key('g'));
        Assert.True(view.GateView.IsOpen);
        var boardDefault = view.GateView.Lines.ToArray();
        Assert.Equal(secondary ? new[] { "11111111 Other" } : new[] { "  22222222  Member", "  33333333  Companion" },
            boardDefault.Skip(1).Take(secondary ? 1 : 2));
        await view.HandleKeyAsync(new Key('q'));
        await Command(view, ":gate");
        Assert.True(view.GateView.IsOpen);
        Assert.Equal(boardDefault, view.GateView.Lines);
        await view.HandleKeyAsync(new Key('q'));
        await Command(view, ":gate " + Id[..8]);
        Assert.Equal(jumped, view.GateView.Lines);

        gate = OtherGate(); // The member left; fall back to the supplied primary gate.
        view.Render(await Model(harness, gate));
        var fallback = view.GateView.Lines.ToArray();
        Assert.Equal("11111111 Other", fallback[1]);
        // A later gate for the same goal must not revive the old pin.
        view.Render(await Model(harness, gate with { OtherLiveGates = [member] }));
        Assert.Equal("11111111 Other", view.GateView.Lines[1]);
        view.Render(await Model(harness, gate));
        Assert.Equal(fallback, view.GateView.Lines);
        await view.HandleKeyAsync(new Key('q'));
        await view.HandleKeyAsync(new Key('g'));
        Assert.True(view.GateView.IsOpen);
        Assert.Equal(fallback, view.GateView.Lines);
        await view.HandleKeyAsync(new Key('q'));
        await Command(view, ":gate");
        Assert.True(view.GateView.IsOpen);
        Assert.Equal(fallback, view.GateView.Lines);

        view.Render(await Model(harness, OwnerConsoleGateProgress.Idle()));
        var idle = view.GateView.Lines.ToArray();
        await view.HandleKeyAsync(new Key('q'));
        await view.HandleKeyAsync(new Key('g'));
        Assert.Equal(idle, view.GateView.Lines);
        Assert.Equal(before, JsonSerializer.Serialize(harness.Kernel.ExportSnapshot()));
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
    }

    [Fact]
    public async Task TypedGate_UnknownMember_ShowsOneActionFailureAndStaysClosed()
    {
        var harness = Harness();
        using var controller = Controller(harness, new Dialogs());
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness, MemberGate()));

        await Command(view, ":gate 99999999");

        Assert.False(view.GateView.IsOpen);
        var notice = Assert.Single(view.NoticeStrip.Visible);
        Assert.Equal("gate 99999999: not in a live gate", notice.Text);
        Assert.Equal(OwnerConsoleNoticeSeverity.Failure, notice.Severity);
        Assert.Equal(OwnerConsoleNoticeSource.Action, notice.Source);
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
    }

    [Fact]
    public async Task GoalDetail_NonMember_HasNoGateActionAndDoesNotClose()
    {
        var harness = Harness();
        var goalOpened = false;
        var dialogs = new Dialogs { OnGoal = async dialog =>
        {
            goalOpened = true;
            Assert.DoesNotContain("Gate view (g)", dialog.Actions);
            var closed = false;
            dialog.CloseRequested += () => closed = true;
            Assert.False(await dialog.HandleKeyAsync('g'));
            Assert.False(closed);
            Assert.Null(dialog.RequestedGateGoalId);
        } };
        using var controller = Controller(harness, dialogs);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(await Model(harness, OtherGate()));
        await view.HandleKeyAsync(Key.Tab);

        await view.HandleKeyAsync(Key.Enter);

        Assert.True(goalOpened);
        Assert.False(view.GateView.IsOpen);
    }

    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member", AgentRole.Developer);
        return harness;
    }

    private static OwnerConsoleGateProgress MemberGate() => new(true, "train",
        [new(Id[..8], "Member"), new("33333333", "Companion")], DateTimeOffset.UnixEpoch,
        TimeSpan.FromMinutes(6), [], .8, 540, 22, 27);
    private static OwnerConsoleGateProgress OtherGate() => MemberGate() with
        { Kind = "solo", Members = [new("11111111", "Other")] };
    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail, harness.Conductor,
            harness.DigestReport, harness.Digest, harness.Clock);
    private static Task<OwnerConsoleViewModel> Model(OwnerConsoleHarness harness, OwnerConsoleGateProgress gate) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Lookup(),
            harness.Clock, gateProgress: _ => gate).BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
    private static async Task Command(OwnerConsoleFullScreenView view, string line)
    {
        await view.HandleKeyAsync(new Key(':'));
        view.CommandLine.Text = line;
        await view.HandleKeyAsync(Key.Enter);
    }
    private sealed class Lookup : IOwnerGoalEpicLookup
    { public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(""); }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal Func<OwnerConsoleGoalDialog, Task>? OnGoal;
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Read-only gate navigation asked for confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Read-only gate navigation asked for input");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected text dialog");
        public async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
        {
            Assert.NotNull(OnGoal);
            dialog.Start(action => action());
            await dialog.LastLoad;
            await OnGoal(dialog);
        }
    }
}
