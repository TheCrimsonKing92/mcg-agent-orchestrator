using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: per-session fakes, fixed clocks and private roots; no initialized terminal.
public sealed class OwnerLandingsGoalDetailTests
{
    private const string FirstId = "11111111-first";
    private const string SecondId = "22222222-second";

    [Fact]
    public async Task RunCommand_AcceptanceLine_OpensGoalAndReshowsSamePage()
    {
        var harness = Harness();
        var page = Page(harness);
        var dialogs = new Dialogs();
        dialogs.OnPage = (_, _) => dialogs.Pages.Count == 1 ? 3 : null;
        var loads = 0;
        using var controller = Controller(harness, dialogs, _ => { loads++; return page; });

        await controller.RunCommandAsync("landings");

        Assert.Equal(1, loads);
        Assert.StartsWith("22222222", Assert.Single(dialogs.Goals));
        Assert.Equal(2, dialogs.Pages.Count);
        Assert.All(dialogs.Pages, shown => Assert.Equal("Landings", shown.Title));
        Assert.Equal(page.Text, dialogs.Pages[0].Text);
        Assert.Equal(page.ChoiceLines, dialogs.Pages[0].ChoiceLines);
        Assert.Equal(dialogs.Pages[0].Text, dialogs.Pages[1].Text);
        Assert.Equal(dialogs.Pages[0].ChoiceLines, dialogs.Pages[1].ChoiceLines);
        Assert.Empty(harness.Conductor.Calls);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task RunCommand_GateRequest_ReturnsWithoutReshowingPage()
    {
        var harness = Harness();
        var dialogs = new Dialogs();
        dialogs.OnPage = (_, _) => dialogs.Pages.Count == 1 ? 3 : null;
        dialogs.OnGoal = async dialog => Assert.True(await dialog.HandleKeyAsync('g'));
        using var controller = Controller(harness, dialogs, _ => Page(harness));
        var gate = new OwnerConsoleGateProgress(true, "train",
            [new(SecondId[..8], "Second"), new("33333333", "Companion")],
            DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(6), [], .8, 540, 22, 27);
        controller.Apply(await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Lookup(), harness.Clock, gateProgress: _ => gate)
            .BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0)));

        await controller.RunCommandAsync("landings");

        Assert.Single(dialogs.Pages);
        Assert.StartsWith("22222222", Assert.Single(dialogs.Goals));
        Assert.Equal(SecondId, controller.TakeRequestedGateGoalId());
        Assert.Empty(harness.Conductor.Calls);
        Assert.Empty(harness.Answers.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task RunCommand_NoValidChoice_MatchesPlainPageBaseline(int? choice)
    {
        var candidate = await RunExitAsync(choice, plain: false);
        var baseline = await RunExitAsync(choice, plain: true);

        Assert.Equal(baseline, candidate);
        Assert.Equal((Pages: 1, Goals: 0, Loads: 1, Conductors: 0, Answers: 0), candidate);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    public void HandleKey_SourceLine_OnlyRowRaisesGoalChoice(int sourceLine, bool opensGoal)
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        var older = fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1));
        var page = OwnerLandingsPage.From(fixture.Load());
        var lines = page.Text.Split('\n');
        Assert.Equal("Enter on a row opens its goal detail.", lines[0]);
        Assert.StartsWith("Undated:", lines[5]);
        using var dialog = new OwnerConsoleTextDialog("Landings", page.Text, page.ChoiceLines);
        dialog.Format(100, 10);
        var target = dialog.Page.SourceLines.ToList().IndexOf(sourceLine);
        Assert.True(target >= 0, "The selected source line must be present after wrapping.");
        for (var row = 0; row < target; row++) Assert.True(dialog.HandleKey(Key.CursorDown));
        int? selected = null;
        dialog.ChoiceAccepted += index => selected = index;

        dialog.HandleKey(Key.Enter);

        if (opensGoal)
        {
            Assert.NotNull(selected);
            Assert.Equal(older.Id, page.ChoiceGoalIds[selected.Value]);
        }
        else Assert.Null(selected);
    }

    private static async Task<(int Pages, int Goals, int Loads, int Conductors, int Answers)>
        RunExitAsync(int? choice, bool plain)
    {
        var harness = Harness();
        var page = Page(harness);
        var dialogs = new Dialogs { OnPage = (_, _) => choice };
        var loads = 0;
        using var controller = Controller(harness, dialogs, _ =>
        {
            loads++;
            return plain ? OwnerLandingsPage.Plain(page.Text) : page;
        });

        await controller.RunCommandAsync("landings");

        return (dialogs.Pages.Count, dialogs.Goals.Count, loads,
            harness.Conductor.Calls.Count, harness.Answers.Calls.Count);
    }

    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(FirstId, "First", AgentRole.Developer);
        harness.AddGoal(SecondId, "Second", AgentRole.Developer);
        return harness;
    }

    private static OwnerLandingsPage Page(OwnerConsoleHarness harness)
    {
        return OwnerLandingsPage.From(new(false, "today", [Row(FirstId, "First"), Row(SecondId, "Second")], 0));

        OwnerLandingsQuery.Row Row(string id, string title) =>
            new(id, title, harness.Clock.GetUtcNow(), "ccccccccc", "solo", "acceptance passed");
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness,
        Dialogs dialogs, Func<string, OwnerLandingsPage> landings) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock, landings: landings);

    private sealed class Lookup : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal Func<string, IReadOnlyList<int>, int?> OnPage { get; set; } = (_, _) => null;
        internal Func<OwnerConsoleGoalDialog, Task>? OnGoal { get; set; }
        internal List<(string Title, string Text, int[] ChoiceLines)> Pages { get; } = [];
        internal List<string> Goals { get; } = [];

        public Task<bool> ConfirmAsync(string title, string text) =>
            throw new InvalidOperationException("Unexpected confirmation during Landings navigation");
        public Task<string?> PromptTextAsync(string title, string text) =>
            throw new InvalidOperationException("Unexpected prompt during Landings navigation");
        public Task ShowTextAsync(string title, string text) =>
            throw new InvalidOperationException("Unexpected text dialog during Landings navigation");

        public Task<int?> ShowPageAsync(string title, string text, IReadOnlyList<int>? choiceLines = null)
        {
            var choices = choiceLines ?? [];
            Pages.Add((title, text, choices.ToArray()));
            return Task.FromResult(OnPage(text, choices));
        }

        public async Task ShowGoalAsync(OwnerConsoleGoalDialog dialog)
        {
            dialog.Start(action => action());
            Goals.Add(dialog.Current.Text);
            if (OnGoal is not null) await OnGoal(dialog);
        }
    }
}
