// Parallel-safe: fixed models and an uninitialized instance application.
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

public sealed class OwnerConsoleGateDialogExpandTests
{
    [Fact]
    public async Task EnterRefreshAndTwoEscapes_PreserveExpansionThenRestoreBoardFocus()
    {
        var harness = new OwnerConsoleHarness();
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(), harness.State,
            harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        var gate = new OwnerConsoleGateProgress(true, "solo", [new("11111111", "")], DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(5),
        [
            new("Passed lane", OwnerConsoleGateLaneState.Done, "passed", "local", TimeSpan.FromSeconds(10), 20, false),
            new("cli [trusted main baseline]", OwnerConsoleGateLaneState.Done, "passed", "local", TimeSpan.FromSeconds(10), 20, false, IsManifestLane: false)
        ], 1, 0, 1, 1);
        var model = new OwnerConsoleViewModel(new(true, 0, 0, 0, null, 0), [], [], [], Gate: gate);
        view.Render(model);
        view.FocusDecisions();
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(new Key('g'));
        Assert.True(view.GateView.IsOpen);
        Assert.False(view.GateView.Expanded);
        Assert.Contains("BASELINE  1 comparison runs: 1 passed (Enter to expand)", view.GateView.Lines);
        Assert.Contains("PASSED  1 lanes (Enter to expand)", view.GateView.Lines);
        Assert.DoesNotContain(view.GateView.Lines, line => line.StartsWith(" + "));

        await view.HandleKeyAsync(Key.Enter);
        Assert.True(view.GateView.Expanded);
        Assert.Contains("BASELINE  1 comparison runs: 1 passed (Enter to collapse)", view.GateView.Lines);
        Assert.Contains("PASSED  1 lanes (Enter to collapse)", view.GateView.Lines);
        Assert.Contains(view.GateView.Lines, line => line.StartsWith(" + Passed lane"));
        Assert.Contains(view.GateView.Lines, line => line.StartsWith(" + cli [trusted main baseline]"));
        view.Render(model with { Gate = gate with { Lanes = gate.Lanes.Select(row => row with { Elapsed = TimeSpan.FromSeconds(99) }).ToArray() } });
        Assert.True(view.GateView.Expanded);
        Assert.Equal(2, view.GateView.Lines.Count(line => line.StartsWith(" + ") && line.Contains("99s /")));
        await view.HandleKeyAsync(Key.Esc);
        Assert.True(view.GateView.IsOpen);
        Assert.False(view.GateView.Expanded);
        Assert.DoesNotContain(view.GateView.Lines, line => line.StartsWith(" + "));
        await view.HandleKeyAsync(Key.Esc);
        Assert.False(view.GateView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);

        await view.HandleKeyAsync(new Key('g'));
        await view.HandleKeyAsync(Key.Enter);
        await view.HandleKeyAsync(Key.Enter);
        Assert.False(view.GateView.Expanded);
        view.GateView.Open(OwnerConsolePane.Board, gate);
        Assert.False(view.GateView.Expanded);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
