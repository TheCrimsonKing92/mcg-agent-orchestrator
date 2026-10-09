using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// Parallel-safe: per-test kernels, in-memory sources and an uninitialized instance application.
public sealed class OwnerConsoleEpicViewTests
{
    [Fact]
    public async Task EPressListsEveryEpicWithBuildCounts()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));

        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.Equal("EPICS · last 24 hours", fixture.View.EpicView.HeaderText);
        AssertSummaries(fixture, Now.AddHours(-24));
        Assert.Contains("landed 1 · in flight 3 (1 verifying, 2 active) · failed 1 · backlog 2 open / 1 done", fixture.View.EpicView.Lines);
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Alpha epic", StringComparison.Ordinal));
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Beta epic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WCyclesWindowAndCountsFollowBuild()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        foreach (var expected in new[]
        {
            (OwnerConsoleEpicWindow.Day, "last 24 hours", (DateTimeOffset?)Now.AddHours(-24), 1),
            (OwnerConsoleEpicWindow.Week, "last 7 days", (DateTimeOffset?)Now.AddDays(-7), 2),
            (OwnerConsoleEpicWindow.AllTime, "all time", (DateTimeOffset?)null, 3),
            (OwnerConsoleEpicWindow.Day, "last 24 hours", (DateTimeOffset?)Now.AddHours(-24), 1)
        })
        {
            Assert.Equal(expected.Item1, fixture.View.EpicView.TimeWindow);
            Assert.Contains(expected.Item2, fixture.View.EpicView.HeaderText);
            AssertSummaries(fixture, expected.Item3);
            Assert.StartsWith($"landed {expected.Item4} ·", fixture.View.EpicView.Lines[1]);
            await fixture.View.HandleKeyAsync(new Key('w'));
        }
    }

    [Fact]
    public async Task EnterOpensPlanInFlightLandedAndTrouble()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        var dialog = fixture.View.EpicView;

        Assert.True(dialog.ShowingDetail);
        Assert.Equal("beta", dialog.SelectedEpicId);
        var lines = dialog.Lines;
        var plan = lines.Skip(2).TakeWhile(line => line != "In flight:").Where(line => line.Length > 0);
        Assert.Equal(Fixture.Plan.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            string.Join(" ", plan).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("b-active  Active beta  Developer", lines);
        Assert.Contains("b-verify  Verifying beta  Tester", lines);
        Assert.Contains("b-landed  Landed beta  -", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Old landed beta", StringComparison.Ordinal));
        Assert.Contains("Rejected by vendor", lines);
        Assert.Contains("Goal parked: waiting on vendor", lines);
        Assert.Contains("no reason recorded", lines);
        var row = fixture.Source.Build(Now.AddHours(-24)).Single(item => item.Epic.Id == "beta");
        Assert.Equal(row.WindowLandedCount, Section(lines, "Landed in window:").Count(line => line.StartsWith("b-", StringComparison.Ordinal)));
        Assert.Equal(row.WindowFailedCount, Section(lines, "Failed:").Count(line => line.StartsWith("b-", StringComparison.Ordinal)));

        await fixture.View.HandleKeyAsync(Key.Esc);
        Assert.True(dialog.IsOpen);
        Assert.False(dialog.ShowingDetail);
        Assert.Equal("beta", dialog.SelectedEpicId);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task CommandAndKeyRestorePreviousPane(int paneValue, int tabs)
    {
        var pane = (OwnerConsolePane)paneValue;
        using var fixture = new Fixture();
        for (var i = 0; i < tabs; i++) await fixture.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(pane, fixture.View.FocusedPane);
        await fixture.View.HandleKeyAsync(new Key('e'));
        var keyLines = fixture.View.EpicView.Lines.ToArray();
        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertClosedAndFocused(fixture.View, pane);

        await fixture.View.HandleKeyAsync(new Key(':'));
        var typedE = new Key('e');
        await fixture.View.HandleKeyAsync(typedE);
        Assert.False(typedE.Handled);
        Assert.False(fixture.View.EpicView.IsOpen);
        fixture.View.CommandLine.Text = ":epics";
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.Equal(keyLines, fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertClosedAndFocused(fixture.View, pane);
        Assert.Contains("e epics", fixture.View.EpicHintText);
        Assert.Contains(":epics", fixture.View.EpicHintText);
        await fixture.View.HandleKeyAsync(new Key('?'));
        var help = Assert.Single(fixture.Dialogs.Texts).Text;
        Assert.Contains(help.Split('\n'), line => line.StartsWith("e: ", StringComparison.Ordinal));
        Assert.Contains(help.Split('\n'), line => line.StartsWith(":epics: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenViewRefreshesCountsAndDetailWithBoardUpdates()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        fixture.Source.Epics[1] = fixture.Source.Epics[1] with { Description = "New reviewed plan" };
        fixture.Source.Goals.Add(new("b-newest", "Active", "New live goal", Now.ToString("O")));
        fixture.Source.Members.Add(new("beta", PortfolioMemberKind.Goal, "b-newest", Now, "test"));
        fixture.Harness.AddGoal("b-newest", "New live goal", AgentRole.Reviewer);
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        Assert.Equal("beta", fixture.View.EpicView.SelectedEpicId);
        Assert.Contains("New reviewed plan", fixture.View.EpicView.Lines);
        Assert.Contains("b-newest  New live goal  Reviewer", fixture.View.EpicView.Lines);

        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertSummaries(fixture, Now.AddHours(-24));
        await fixture.View.HandleKeyAsync(Key.Esc);
        var calls = fixture.Source.Calls;
        fixture.View.Render(fixture.Model);
        Assert.Equal(calls, fixture.Source.Calls);
    }

    [Fact]
    public async Task OpenViewIsModalAndLoadFailureCanRecover()
    {
        using var fixture = new Fixture();
        fixture.Source.Error = new InvalidOperationException("portfolio unavailable");
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("Could not load epics: portfolio unavailable. Press r to retry.", fixture.View.EpicView.Lines);
        foreach (var key in new[] { new Key('q'), new Key(':'), new Key('?'), Key.Tab })
        {
            await fixture.View.HandleKeyAsync(key);
            Assert.True(key.Handled);
        }
        Assert.False(fixture.Controller.QuitRequested);
        Assert.Empty(fixture.Dialogs.Texts);
        fixture.Source.Error = null;
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        AssertSummaries(fixture, Now.AddHours(-24));
    }

    [Fact]
    public async Task EmptyEpicRemainsVisibleAndEnterOnEmptyListDoesNothing()
    {
        using var fixture = new Fixture();
        fixture.Source.Epics.Clear();
        fixture.Source.Epics.Add(new("empty", "Empty epic", null, Now, "test", Now, "test"));
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("landed 0 · in flight 0 (0 verifying, 0 active) · failed 0 · backlog 0 open / 0 done", fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Contains("(no plan of record)", fixture.View.EpicView.Lines);
        fixture.Source.Epics.Clear();
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.False(fixture.View.EpicView.ShowingDetail);
        Assert.Equal(["No epics are defined yet. Create one with epic-add."], fixture.View.EpicView.Lines);
    }

    [Fact]
    public async Task DetailWrapsOnResizeAndPagesThroughFullPlan()
    {
        using var fixture = new Fixture();
        fixture.View.Window.Layout(new System.Drawing.Size(46, 18));
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        fixture.View.Window.Layout(new System.Drawing.Size(46, 18));
        var dialog = fixture.View.EpicView;
        Assert.True(dialog.BodyPane.Viewport.Width > 0);
        Assert.All(dialog.Lines, line => Assert.True(line.GetColumns() <= dialog.BodyPane.Viewport.Width, line));
        var narrowPlanLines = dialog.Lines.TakeWhile(line => line != "In flight:").Count();
        await fixture.View.HandleKeyAsync(Key.PageDown);
        Assert.True(dialog.BodyPane.SelectedItem > 0);
        await fixture.View.HandleKeyAsync(Key.PageUp);
        Assert.Equal(0, dialog.BodyPane.SelectedItem);
        fixture.View.Window.Layout(new System.Drawing.Size(90, 18));
        Assert.True(dialog.Lines.TakeWhile(line => line != "In flight:").Count() < narrowPlanLines);
        Assert.All(dialog.Lines, line => Assert.True(line.GetColumns() <= dialog.BodyPane.Viewport.Width, line));
    }

    [Fact(Timeout = 30000)]
    public async Task BoardUpdatesCoalesceDuringPendingEpicRead()
    {
        using var fixture = new Fixture();
        fixture.Source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        // LoadStarted is the synchronization event; the test timeout only detects a hang.
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var oldRows = fixture.Source.Build(Now.AddHours(-24));
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Updated alpha" };
        fixture.View.Render(fixture.Model);
        var pending = fixture.View.EpicView.LastLoad;
        fixture.View.Render(fixture.Model);
        Assert.Same(pending, fixture.View.EpicView.LastLoad);
        Assert.Equal(1, fixture.Source.Calls);
        var release = fixture.Source.Pending;
        fixture.Source.Pending = null;
        release.SetResult(oldRows);
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Source.Calls);
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Updated alpha", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30000)]
    public async Task ClosingCancelsPendingReadAndRejectsLateResults()
    {
        using var fixture = new Fixture();
        fixture.Source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.View.HandleKeyAsync(Key.Esc);
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        var lines = fixture.View.EpicView.Lines.ToArray();
        fixture.Source.Pending.SetResult(fixture.Source.Build(null));
        fixture.View.Render(fixture.Model);
        AssertClosedAndFocused(fixture.View, OwnerConsolePane.Decisions);
        Assert.Equal(lines, fixture.View.EpicView.Lines);
        Assert.Equal(1, fixture.Source.Calls);
    }

    [Fact(Timeout = 30000)]
    public async Task Open_HeldLoad_ShowsLoadingBeforeRowsAndLocalUpdateTime()
    {
        using var fixture = new Fixture();
        fixture.View.Window.Layout(new System.Drawing.Size(120, 30));
        var pending = fixture.Source.HoldNextLoad();
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var dialog = fixture.View.EpicView;

        Assert.True(dialog.Visible);
        Assert.Equal(["Loading epics (last 24 hours)…"], dialog.Lines);
        Assert.Contains("loading…", dialog.HeaderLine);
        Assert.DoesNotContain("r retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await opening.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("> Alpha epic", dialog.Lines);
        AssertSummaries(fixture, Now.AddHours(-24));
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
        Assert.DoesNotContain("loading…", dialog.HeaderLine);
    }

    [Fact(Timeout = 30000)]
    public async Task WindowChange_HeldLoad_ClearsRowsAndNamesNewWindow()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("> Alpha epic", fixture.View.EpicView.Lines);
        var pending = fixture.Source.HoldNextLoad();
        var changing = fixture.View.HandleKeyAsync(new Key('w'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Loading epics (last 7 days)…"], fixture.View.EpicView.Lines);
        Assert.Contains("loading…", fixture.View.EpicView.HeaderLine);
        fixture.Source.Release(pending, Now.AddDays(-7));
        await changing.WaitAsync(TestContext.Current.CancellationToken);
        AssertSummaries(fixture, Now.AddDays(-7));
    }

    [Fact(Timeout = 30000)]
    public async Task Enter_HeldDetailLoad_ClearsListAndNamesSelectedEpic()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        Assert.Contains("> Beta epic", fixture.View.EpicView.Lines);
        var pending = fixture.Source.HoldNextLoad();
        var entering = fixture.View.HandleKeyAsync(Key.Enter);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Loading Beta epic…"], fixture.View.EpicView.Lines);
        Assert.Contains("loading…", fixture.View.EpicView.HeaderLine);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await entering.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Beta epic", fixture.View.EpicView.Lines[0]);
        Assert.Contains("Plan of record:", fixture.View.EpicView.Lines);
        Assert.Contains("updated 01:00:00", fixture.View.EpicView.HeaderLine);
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_Retry_ShowsReasonAndClearsFailureAndRetryHint(bool detail)
    {
        using var fixture = new Fixture();
        if (detail)
        {
            await fixture.View.HandleKeyAsync(new Key('e'));
            await fixture.View.HandleKeyAsync(Key.CursorDown);
        }
        fixture.Source.Error = new InvalidOperationException("portfolio unavailable.");
        await fixture.View.HandleKeyAsync(detail ? Key.Enter : new Key('e'));
        var dialog = fixture.View.EpicView;
        Assert.Equal(["Could not load epics: portfolio unavailable. Press r to retry."], dialog.Lines);
        Assert.Contains("failed", dialog.HeaderLine);
        Assert.Contains("r retry", dialog.HintText);
        fixture.Source.Error = null;
        var pending = fixture.Source.HoldNextLoad();
        var retrying = fixture.View.HandleKeyAsync(new Key('r'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal([detail ? "Loading Beta epic…" : "Loading epics (last 24 hours)…"], dialog.Lines);
        Assert.DoesNotContain("r retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await retrying.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(detail ? "Beta epic" : "> Alpha epic", dialog.Lines);
        Assert.DoesNotContain("Could not load", string.Join(" ", dialog.Lines));
        Assert.DoesNotContain("r retry", dialog.HintText);
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
    }

    [Fact]
    public async Task Open_MissingSource_ShowsUnavailableInsteadOfEmptyPortfolio()
    {
        using var fixture = new Fixture(wired: false);
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Equal(["Epic data is not available in this console session."], fixture.View.EpicView.Lines);
        Assert.DoesNotContain("No epics", string.Join(" ", fixture.View.EpicView.Lines));
        Assert.Contains("unavailable", fixture.View.EpicView.HeaderLine);
        Assert.DoesNotContain("r retry", fixture.View.EpicView.HintText);
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact]
    public async Task Open_ZeroEpics_ShowsCreationHint()
    {
        using var fixture = new Fixture();
        fixture.Source.Epics.Clear();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Equal(["No epics are defined yet. Create one with epic-add."], fixture.View.EpicView.Lines);
        Assert.Contains("updated 01:00:00", fixture.View.EpicView.HeaderLine);
        Assert.Equal(1, fixture.Source.Calls);
    }

    [Theory]
    [InlineData(false, "last 24 hours")]
    [InlineData(true, "last 7 days")]
    public async Task Window_OldActivity_ShowsWidenHintAboveEpics(bool week, string label)
    {
        using var fixture = new Fixture();
        fixture.Source.Goals.RemoveAll(goal => goal.Id != "a-oldest");
        await fixture.View.HandleKeyAsync(new Key('e'));
        if (week) await fixture.View.HandleKeyAsync(new Key('w'));
        var dialog = fixture.View.EpicView;
        Assert.Equal($"No epic activity in the {label}; press w to widen.", dialog.Lines[0]);
        Assert.Equal("> Alpha epic", dialog.Lines[1]);
        Assert.Contains("  Beta epic", dialog.Lines);
        Assert.Equal(1, dialog.BodyPane.SelectedItem);
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("> Beta epic", dialog.Lines[4]);
        Assert.Equal(4, dialog.BodyPane.SelectedItem);
        if (!week) await fixture.View.HandleKeyAsync(new Key('w'));
        await fixture.View.HandleKeyAsync(new Key('w'));
        Assert.DoesNotContain("No epic activity", string.Join(" ", dialog.Lines));
        AssertSummaries(fixture, null);
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelApplied_HeldRefresh_KeepsContentWithRefreshingHeader(bool detail)
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        if (detail) await fixture.View.HandleKeyAsync(Key.Enter);
        var dialog = fixture.View.EpicView;
        var currentLines = dialog.Lines.ToArray();
        var pending = fixture.Source.HoldNextLoad();
        fixture.View.Render(fixture.Model);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var refreshing = dialog.LastLoad;
        Assert.Equal(currentLines, dialog.Lines);
        Assert.Contains("refreshing…", dialog.HeaderLine);
        Assert.DoesNotContain("r retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await refreshing.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
        Assert.DoesNotContain("refreshing…", dialog.HeaderLine);
    }

    [Fact(Timeout = 30000)]
    public async Task Reopen_HeldLoad_ClearsPreviousContent()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("> Alpha epic", fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Esc);
        var pending = fixture.Source.HoldNextLoad();
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Loading epics (last 24 hours)…"], fixture.View.EpicView.Lines);
        Assert.Contains("loading…", fixture.View.EpicView.HeaderLine);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        AssertSummaries(fixture, Now.AddHours(-24));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T22:00:00Z");

    private static IEnumerable<string> Section(IReadOnlyList<string> lines, string heading) =>
        lines.SkipWhile(line => line != heading).Skip(1).TakeWhile(line => !line.EndsWith(':'));

    private static void AssertSummaries(Fixture fixture, DateTimeOffset? since)
    {
        foreach (var row in fixture.Source.Build(since))
        {
            // Explicit fields keep this assertion independent of the production formatter.
            var expected = $"landed {row.WindowLandedCount ?? row.LandedCount} · in flight {row.ActiveCount + row.VerifyingCount} ({row.VerifyingCount} verifying, {row.ActiveCount} active) · failed {row.WindowFailedCount ?? row.FailedCount} · backlog {row.BacklogOpenCount} open / {row.BacklogDoneCount} done";
            Assert.Contains(expected, fixture.View.EpicView.Lines);
        }
    }

    private static void AssertClosedAndFocused(OwnerConsoleFullScreenView view, OwnerConsolePane pane)
    {
        Assert.False(view.EpicView.IsOpen);
        Assert.Equal(pane, view.FocusedPane);
        Assert.True(pane switch
        {
            OwnerConsolePane.Decisions => view.DecisionsPane.HasFocus,
            OwnerConsolePane.Board => view.BoardPane.HasFocus,
            _ => view.ActivityPane.HasFocus
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Plan = "Purpose: show the whole epic plan with owner decisions and phases.\nPhase one: render the plan of record with enough detail to require wrapping at the available window width while keeping every word accessible.\nNext: review with Miles. Last reviewed: 2026-10-08.";
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly Source Source = new();
        internal readonly Dialogs Dialogs = new();
        internal readonly IApplication App = Terminal.Gui.App.Application.Create();
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly OwnerConsoleViewModel Model = new(new(true, 3, 0, 0, null, 0), [], [], []);

        internal Fixture(bool wired = true)
        {
            Source.Epics.AddRange([
                new("alpha", "Alpha epic", null, Now, "test", Now, "test", "Alpha plan"),
                new("beta", "Beta epic", null, Now, "test", Now, "test", Plan)]);
            Add("alpha", "a-active", "Active alpha", GoalStatus.Active, -2);
            Add("alpha", "a-second", "Second active alpha", GoalStatus.WaitingForHuman, -2);
            Add("alpha", "a-verify", "Verifying alpha", GoalStatus.Verifying, -2);
            Add("alpha", "a-landed", "Landed alpha", GoalStatus.Completed, -2);
            Add("alpha", "a-weekly", "Weekly landed alpha", GoalStatus.Completed, -72);
            Add("alpha", "a-oldest", "Old landed alpha", GoalStatus.Completed, -720);
            Add("alpha", "a-failed", "Failed alpha", GoalStatus.Failed, -2);
            Add("alpha", "a-oldfail", "Old failed alpha", GoalStatus.Failed, -72);
            Add("alpha", "a-parked", "Parked alpha", GoalStatus.Parked, -720);
            Add("alpha", "a-closed", "Closed alpha", GoalStatus.Cancelled, -2);
            Add("alpha", "a-verified", "Verified alpha", GoalStatus.Verified, -2);
            Source.Members.Add(new("alpha", PortfolioMemberKind.Goal, "missing", Now, "test"));
            foreach (var (id, status) in new[] { ("todo", BacklogItemStatus.Open), ("todo2", BacklogItemStatus.Open), ("done", BacklogItemStatus.Done) })
            {
                Source.Backlog.Add(new(id, id, "body", status, Now, Now, null));
                Source.Members.Add(new("alpha", PortfolioMemberKind.BacklogItem, id, Now, "test"));
            }
            Add("beta", "b-active", "Active beta", GoalStatus.Active, -2);
            Add("beta", "b-verify", "Verifying beta", GoalStatus.Verifying, -2);
            Add("beta", "b-landed", "Landed beta", GoalStatus.Completed, -2);
            Add("beta", "b-oldest", "Old landed beta", GoalStatus.Completed, -72);
            Add("beta", "b-failed", "Failed beta", GoalStatus.Failed, -2);
            Add("beta", "b-noinfo", "Missing failed beta", GoalStatus.Failed, -2);
            Add("beta", "b-parked", "Parked beta", GoalStatus.Parked, -720);
            Harness.AddGoal("b-active", "Active beta", AgentRole.Developer);
            Harness.AddGoal("b-verify", "Verifying beta", AgentRole.Tester);
            var failed = Harness.AddGoal("b-failed", "Failed beta", AgentRole.Tester);
            Harness.Kernel.EscalateTaskFailure(failed.Id, failed.Tasks.Single().Id, "Rejected by vendor");
            var parked = Harness.AddGoal("b-parked", "Parked beta", AgentRole.Developer);
            Harness.Kernel.ParkGoal(parked.Id, "waiting on vendor");
            Controller = new(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Harness.Tail,
                Harness.Conductor, Harness.DigestReport, Harness.Digest, new Clock(), wired ? Source : null);
            View = new(App, Controller, () => Task.CompletedTask);
            View.Render(Model);
            View.FocusDecisions();
        }

        private void Add(string epic, string id, string title, GoalStatus status, int hours)
        {
            Source.Goals.Add(new(id, status.ToString(), title, Now.AddHours(hours).ToString("O")));
            Source.Members.Add(new(epic, PortfolioMemberKind.Goal, id, Now, "test"));
        }

        public void Dispose() { View.Dispose(); App.Dispose(); }
    }

    private sealed class Source : IOwnerConsoleEpicSource
    {
        internal readonly List<PortfolioEpic> Epics = [];
        internal readonly List<PortfolioEpicMember> Members = [];
        internal readonly List<GoalSummary> Goals = [];
        internal readonly List<BacklogItem> Backlog = [];
        internal int Calls;
        internal Exception? Error;
        internal TaskCompletionSource LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyList<EpicProgressRollup>>? Pending;
        internal TaskCompletionSource<IReadOnlyList<EpicProgressRollup>> HoldNextLoad()
        {
            LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal void Release(TaskCompletionSource<IReadOnlyList<EpicProgressRollup>> pending, DateTimeOffset? since)
        {
            Pending = null;
            pending.SetResult(Build(since));
        }
        internal IReadOnlyList<EpicProgressRollup> Build(DateTimeOffset? since) =>
            EpicProgressReadModel.Build(Epics, [], Members, Goals, Backlog, since);
        public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token)
        {
            Calls++;
            LoadStarted.TrySetResult();
            if (Pending is { } pending) return pending.Task;
            return Error is null ? Task.FromResult(Build(since)) : Task.FromException<IReadOnlyList<EpicProgressRollup>>(Error);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) { Texts.Add((title, text)); return Task.CompletedTask; }
    }
}
