using System.Drawing;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

// Parallel-safe: instance-owned kernels, injected data reads, fixed zones and uninitialized GUI views.
public sealed class OwnerConsoleSelectedPreviewTests
{
    private const string Id = "11111111-selected";
    private const string Title = "Improve the owner console";
    private const string Stage = "waiting for a free worker";
    private static readonly string Head = new('a', 40);
    private static readonly string Basis = new('b', 40);
    private static readonly string OtherHead = new('c', 40);
    private static readonly DateTimeOffset At = new(2026, 10, 28, 14, 5, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("two-files", "Changes 2 files in src/App: Feature.cs and 1 more", Stage)]
    [InlineData("one-file", "Changes 1 file in src/App: Feature.cs", Stage)]
    [InlineData("three-areas", "Changes 3 files in 3 areas: Feature.cs and 2 more", Stage)]
    [InlineData("no-commit", "No committed changes yet", Stage)]
    [InlineData("empty-range", "No committed changes yet", Stage)]
    [InlineData("unreadable", "Change list unavailable: could not read the commits", Stage)]
    [InlineData("read-throws", "Change list unavailable: could not read the commits", Stage)]
    [InlineData("gate-passed", "Changes 1 file in src/App: Feature.cs", Stage + " · gate passed 14:05")]
    [InlineData("gate-failed", "Changes 1 file in src/App: Feature.cs", Stage + " · gate failed 14:05: owner console tests")]
    [InlineData("focused-passed", "Changes 1 file in src/App: Feature.cs", Stage + " · focused tests passed 14:05")]
    [InlineData("focused-failed", "Changes 1 file in src/App: Feature.cs", Stage + " · focused tests failed 14:05")]
    [InlineData("no-evidence", "Changes 1 file in src/App: Feature.cs", Stage)]
    public async Task LoadedFixturesShowExactPlainLines(string scenario, string changes, string progress)
    {
        var goal = GoalFor(scenario);
        var diffReads = 0;
        var acceptanceReads = 0;
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, head, _) =>
        {
            diffReads++;
            Assert.Equal(Basis, basis);
            Assert.Equal(Head, head);
            if (scenario == "read-throws") throw new IOException("internal commit read error");
            return Task.FromResult(scenario switch
            {
                "two-files" => new CommittedGoalChanges(true, ["M src/App/Feature.cs", "A src/App/Other.cs"], 2),
                "three-areas" => new(true, ["M src/App/Feature.cs", "A tests/FeatureTests.cs", "M docs/feature.md"], 3),
                "empty-range" => new(true, [], 0),
                "unreadable" => new(false, [], 0, "internal git error"),
                _ => new(true, ["M src/App/Feature.cs"], 1)
            });
        }, acceptance: (id, head, _, _) =>
        {
            acceptanceReads++;
            Assert.Equal(goal.Id, id);
            Assert.Equal(Head, head);
            return Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis,
                scenario is "gate-passed" or "gate-failed" ? Outcome(goal, scenario == "gate-passed") : null));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));

        var summary = await query.LoadAsync(goal, Token);
        Assert.Equal(scenario == "no-commit" ? 0 : 2, diffReads);
        Assert.Equal(scenario == "no-commit" ? 0 : 1, acceptanceReads);
        var lines = OwnerConsoleSelectedPreview.Lines(new(Id, summary), Stage, TimeZoneInfo.Utc, 160);
        Assert.Equal(new[] { Title, changes, progress }, lines);
        AssertPlain(lines);
        // A refresh marker belongs to detail diagnostics, never to the SELECTED panel.
        var staleLines = OwnerConsoleSelectedPreview.Lines(new(Id, summary.AsStale()), Stage, TimeZoneInfo.Utc, 160);
        Assert.Equal(lines, staleLines);
        AssertPlain(staleLines);
    }

    [Theory]
    [InlineData("M Feature.cs", "Changes 1 file in .: Feature.cs")]
    [InlineData("A src\\App\\Feature.cs", "Changes 1 file in src/App: Feature.cs")]
    [InlineData("M src/App/My Feature.cs", "Changes 1 file in src/App: My Feature.cs")]
    public async Task PathsKeepFileNamesAndNormalizeAreas(string file, string expected)
    {
        var query = new OwnerGoalChangeSummaryQuery(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, [file], 1)),
            mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var summary = await query.LoadAsync(GoalFor("one-file"), Token);
        Assert.Equal(expected, OwnerConsoleSelectedPreview.Lines(new(Id, summary), Stage, TimeZoneInfo.Utc, 160)[1]);
    }

    [Theory]
    [InlineData(true, "gate passed 14:05")]
    [InlineData(false, "gate failed 14:05: owner console tests")]
    public async Task FullGateTakesPrecedenceOverNewerFocusedEvidence(bool passed, string expected)
    {
        var goal = GoalFor("focused-passed", [Focused(At.AddHours(1), false)]);
        var query = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) =>
            Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis, Outcome(goal, passed))));
        var summary = await query.LoadAsync(goal, Token);
        Assert.Equal(Stage + " · " + expected,
            OwnerConsoleSelectedPreview.Lines(new(Id, summary), Stage, TimeZoneInfo.Utc, 160)[2]);
    }

    [Theory]
    [InlineData("other-goal")]
    [InlineData("other-commit")]
    [InlineData("other-main")]
    [InlineData("in-progress")]
    [InlineData("unreadable")]
    public async Task IneligibleGateFallsBackToLatestAcceptedCurrentCommitEvidence(string scenario)
    {
        var goal = GoalFor("focused-passed",
        [
            Focused(At, true),
            Focused(At.AddMinutes(-10), false),
            Focused(At.AddMinutes(10), false, head: OtherHead),
            Focused(At.AddMinutes(20), false, accepted: false)
        ]);
        var outcome = Outcome(goal, false);
        outcome = scenario switch
        {
            "other-goal" => outcome with { GoalId = new GoalId("22222222-other") },
            "other-commit" => outcome with { BranchHeadSha = OtherHead },
            "other-main" => outcome with { MainHeadSha = OtherHead },
            "in-progress" => outcome with { Status = GoalOperationStatus.Begin, AcceptanceOutcome = "running" },
            _ => outcome
        };
        var query = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) => scenario == "unreadable"
            ? throw new IOException("acceptance lookup failed")
            : Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis, outcome)));
        var summary = await query.LoadAsync(goal, Token);
        Assert.Equal(Stage + " · focused tests passed 14:05",
            OwnerConsoleSelectedPreview.Lines(new(Id, summary), Stage, TimeZoneInfo.Utc, 160)[2]);
    }

    [Theory]
    [InlineData(true, "gate failed 16:05")]
    [InlineData(false, "focused tests passed 16:05")]
    public async Task EvidenceUsesLocalTimeAndFailedGateCanOmitCheckName(bool gate, string expected)
    {
        var goal = GoalFor("focused-passed");
        var query = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) => Task.FromResult(
            new OwnerGoalAcceptanceEvidence(Basis, gate ? Outcome(goal, false) with { FailedCheckNames = ["", " "] } : null)));
        var summary = await query.LoadAsync(goal, Token);
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus-two", TimeSpan.FromHours(2), "test-plus-two", "test-plus-two");
        Assert.Equal(Stage + " · " + expected, OwnerConsoleSelectedPreview.Lines(new(Id, summary), Stage, zone, 160)[2]);
    }

    [Theory]
    [InlineData(null, "Loading…")]
    [InlineData("goal unavailable", "Details unavailable: the goal was not found")]
    [InlineData("IOException", "Details unavailable: could not load the goal")]
    [InlineData("mismatched goal identity", "Details unavailable: could not load the goal")]
    public void LoadingAndErrorsHaveOnePlainLine(string? error, string expected)
    {
        var preview = new OwnerConsoleSelectedGoalPreview(Id, null, error);
        Assert.Equal(new[] { expected }, OwnerConsoleSelectedPreview.Lines(preview, Stage, TimeZoneInfo.Utc, 100));
        Assert.Equal("SELECTED 11111111", OwnerConsoleSelectedPreview.Title(preview.GoalId));
    }

    [Theory]
    [InlineData(60, "Changes 2 files in src/Mcg.AgentOrchestrator.Infrastructu…")]
    [InlineData(100, "Changes 2 files in src/Mcg.AgentOrchestrator.Infrastructure/Workspaces")]
    public async Task PanelWidthsDropFileAndEvidenceBeforeCountAndStage(int panelWidth, string expectedChanges)
    {
        var file = new string('F', 100) + ".cs";
        var query = new OwnerGoalChangeSummaryQuery(changes: (_, _, _) => Task.FromResult(
            new CommittedGoalChanges(true, ["M src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/" + file,
                "A src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/Other.cs"], 2)),
            acceptance: (id, _, _, _) => Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis,
                new("width-test", id, "acceptance", GoalOperationStatus.Failed, At, null,
                    BranchHeadSha: Head, MainHeadSha: Basis, AcceptanceOutcome: "failed", FailedCheckNames: [new string('E', 100)]))),
            mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var summary = await query.LoadAsync(GoalFor("one-file"), Token);
        var preview = new OwnerConsoleSelectedGoalPreview(Id, summary);
        var stage = "waiting for the build lock";
        var lines = OwnerConsoleSelectedPreview.Lines(preview, stage, TimeZoneInfo.Utc, panelWidth - 2);
        Assert.Equal(new[] { Title, expectedChanges, stage }, lines);
        Assert.All(lines, line => Assert.True(line.GetColumns() <= panelWidth - 2));
        Assert.DoesNotContain(file, lines[1]);
        Assert.DoesNotContain(":", lines[1]);
        Assert.DoesNotContain("and 1 more", lines[1]);
        Assert.DoesNotContain("·", lines[2]);
        var wide = OwnerConsoleSelectedPreview.Lines(preview, stage, TimeZoneInfo.Utc, 400);
        Assert.Contains(file + " and 1 more", wide[1]);
        Assert.Contains(" · gate failed 14:05:", wide[2]);
    }

    [Fact]
    public async Task FullScreenFramesSelectionAndUsesTheBoardStage()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = new OwnerConsoleHarness(clock);
        var goal = harness.AddGoal(Id, "# " + Title + "\nMore detail", AgentRole.Developer);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = new OwnerGoalChangeSummaryQuery(promotion: async (_, token) =>
        {
            loaded.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        });
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers,
            new Dialogs(), harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, harness.Tail, summary: summary));
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, clock: clock);
        var model = new OwnerConsoleViewModel(new(false, 1, 0, 0, null, 0), [],
            [new("11111111", "Console", Title, "Active", Stage, null, goal.Id.Value)], []);
        view.Render(model);
        try
        {
            await TestHangGuard.WaitAsync(loaded.Task, "selected summary load entered");
            view.Window.Layout(new Size(100, 40));
            var frame = Assert.Single(view.Window.SubViews.OfType<FrameView>(), frame => frame.Title == "SELECTED 11111111");
            Assert.Equal("Loading…", Assert.Single(frame.SubViews.OfType<Label>()).Text);
        }
        finally { release.TrySetResult(); }
        await TestHangGuard.WaitAsync(view.PreviewLoad, "selected panel publication");
        Assert.Equal(string.Join('\n', Title, "No committed changes yet", Stage), view.PreviewText);
        var selected = Assert.Single(view.Window.SubViews.OfType<FrameView>(), frame => frame.Title == "SELECTED 11111111");
        var board = Assert.Single(view.Window.SubViews.OfType<FrameView>(), frame => frame.Title.StartsWith("BOARD", StringComparison.Ordinal));
        var activity = Assert.Single(view.Window.SubViews.OfType<FrameView>(), frame => frame.Title.StartsWith("ACTIVITY", StringComparison.Ordinal));
        Assert.Equal(board.Frame.Bottom, selected.Frame.Y);
        Assert.Equal(5, selected.Frame.Height);
        Assert.Equal(selected.Frame.Bottom, activity.Frame.Y);
        Assert.DoesNotContain("Enter", view.PreviewText);

        view.Render(model with { Board = [model.Board[0] with { Stage = "question for you" }] });
        await TestHangGuard.WaitAsync(view.PreviewLoad, "selected panel refreshed after BOARD stage changed");
        Assert.Equal(string.Join('\n', Title, "No committed changes yet", "question for you"), view.PreviewText);
    }

    private static Goal GoalFor(string scenario, TaskVerificationSnapshot[]? records = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(Id), "# " + Title + "\nMore detail",
            [new(TaskId.New(), "Build", AgentRole.Developer)]);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        records ??= scenario is "focused-passed" or "focused-failed" ? [Focused(At, scenario == "focused-passed")] : [];
        snapshot = snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task with
            {
                LastDispatch = scenario == "no-commit" ? null : new("worker", "worker", "in-memory", At.AddHours(-1),
                    BaseCommit: Basis, ResultCommit: Head),
                VerificationHistory = records
            }).ToArray()
        };
        return AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).GetGoal(goal.Id);
    }

    private static GoalOperationJournalEntry Outcome(Goal goal, bool passed) => new("full-gate", goal.Id, "acceptance",
        passed ? GoalOperationStatus.Completed : GoalOperationStatus.Failed, At, null,
        BranchHeadSha: Head, MainHeadSha: Basis, AcceptanceOutcome: passed ? "passed" : "failed",
        FailedCheckNames: passed ? [] : ["owner console tests", "later check"]);

    private static TaskVerificationSnapshot Focused(DateTimeOffset at, bool passed, bool accepted = true, string? head = null) =>
        new("conductor focused evidence", "in-memory", 0, "", "", at, FindingEvidenceReceipts:
        [new("focused-" + at.Ticks, head ?? Head,
            new([new("Infrastructure.Tests", "FullyQualifiedName~FeatureTests")]), accepted, passed, "focused result")]);

    private static void AssertPlain(IReadOnlyList<string> lines)
    {
        Assert.Equal(3, lines.Count);
        foreach (var line in lines)
        {
            Assert.DoesNotMatch(@"[0-9a-fA-F]{40}", line);
            foreach (var term in new[] { "receipt", "candidate", "brief v", "stale", "association", "applicable" })
                Assert.DoesNotContain(term, line, StringComparison.OrdinalIgnoreCase);
            foreach (var owner in Enum.GetNames<ConductorHoldOwner>()) Assert.DoesNotContain(owner, line);
        }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("No dialogs expected");
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes");
    }
}
