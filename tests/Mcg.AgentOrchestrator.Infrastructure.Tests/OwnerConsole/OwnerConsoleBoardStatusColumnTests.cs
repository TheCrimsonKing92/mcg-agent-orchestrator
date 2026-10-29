using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Views;

// Parallel-safe: in-memory kernels, injected clocks and uninitialized per-test GUI instances.
public sealed class OwnerConsoleBoardStatusColumnTests
{
    [Theory]
    [InlineData(GoalStatus.Verified, "waiting for gate")]
    [InlineData(GoalStatus.Verifying, "in gate: ")]
    [InlineData(GoalStatus.Failed, "failed: ")]
    [InlineData(GoalStatus.Active, "in progress: Developer")]
    public async Task Board_StatusFixture_RendersOnePlainStatusColumn(GoalStatus status, string prefix)
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Fixture(clock, [status]);
        var goal = Assert.Single(harness.Kernel.Goals);
        var gate = status == GoalStatus.Verifying
            ? new OwnerConsoleGateProgress(true, "cohort", [new(goal.Id.Value, "Gate fixture")],
                clock.Now.AddMinutes(-10), TimeSpan.FromMinutes(10), [], .5, 360, 2, 4)
            : null;
        if (status == GoalStatus.Verifying)
        {
            Assert.True(gate!.Live);
            Assert.Equal(4, gate.ForMember(goal.Id.Value)!.TotalCount);
        }
        var model = await Builder(harness, clock, gate).BuildBoardAsync();
        var row = Assert.Single(model.Board);
        Assert.Equal(status.ToString(), row.State);
        Assert.StartsWith(prefix, row.Stage);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);

        view.Render(model);

        AssertColumns(view.BoardTable);
        Assert.Equal(row.Stage, view.BoardTable.Rows[0]["STATUS"]);
        Assert.StartsWith(prefix, Assert.IsType<string>(view.BoardTable.Rows[0]["STATUS"]));
        AssertNoEnumCells(view.BoardTable);
    }

    [Fact]
    public void Board_RecentCompletedRow_RendersLandedReasonThroughDescriber()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Fixture(clock, [GoalStatus.Completed]);
        var goal = Assert.Single(harness.Kernel.Goals);
        var at = Assert.Single(goal.Timeline).OccurredAt;
        Assert.Equal(TimeSpan.FromMinutes(10), clock.Now - at);
        // The builder excludes Completed. Exercise the requested terminal presentation directly,
        // preserving that existing board admission rule (also pinned by the every-status test).
        var stage = OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.RecentTerminal, at, goal.Id.Value), null, clock.Now);
        OwnerConsoleBoardRow row = new(goal.Id.Value, "", "Completed fixture", goal.Status.ToString(),
            stage, clock.Now - at, goal.Id.Value, true);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);

        OwnerConsoleBoardRenderer.Render(view.BoardTable, view.BoardPane, [row], 0, 0);

        AssertColumns(view.BoardTable);
        Assert.Equal("landed: recorded reason", view.BoardTable.Rows[0]["STATUS"]);
        AssertNoEnumCells(view.BoardTable);
    }

    [Fact]
    public async Task Board_IdleDeveloper_UsesInProgressPrefixInOtherRank()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Fixture(clock, [GoalStatus.Active]);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Pending }).ToArray()
        }).ToArray() });

        var row = Assert.Single((await Builder(harness, clock).BuildBoardAsync()).Board);

        Assert.Equal(OwnerConsoleAttention.Other, row.SortKey.Rank);
        Assert.Equal("in progress: Developer", row.Stage);
    }

    [Fact]
    public async Task Board_EveryStatus_PreservesAdmissionOrderDimmingAndFailedCount()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Fixture(clock, Enum.GetValues<GoalStatus>());
        Assert.Equal(Enum.GetValues<GoalStatus>().Order(), harness.Kernel.Goals.Select(goal => goal.Status).Order());

        var model = await Builder(harness, clock).BuildBoardAsync();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        view.Render(model);

        // Existing attention order; Completed, Cancelled and Superseded remain absent.
        var expected = new[] { GoalStatus.WaitingForHuman, GoalStatus.Verifying, GoalStatus.Verified,
            GoalStatus.Active, GoalStatus.Draft, GoalStatus.AcceptanceFailed, GoalStatus.Failed, GoalStatus.Parked };
        var expectedIds = expected.Select(Id).ToArray();
        Assert.Equal(expectedIds, model.Board.Select(row => row.GoalId));
        Assert.Equal(expectedIds, view.BoardTable.Rows.Cast<DataRow>().Select(row => (string)row["GOAL"]));
        Assert.Equal(expected.Select(status => status.ToString()), model.Board.Select(row => row.State));
        Assert.Equal(new[] { false, false, false, false, false, false, true, true }, model.Board.Select(row => row.Dimmed));
        Assert.Equal(6, model.Status.ActiveGoals);
        Assert.Equal(1, model.Status.FailedToday);
        Assert.Contains("failed today: 1", view.StatusText);
        AssertColumns(view.BoardTable);
        AssertNoEnumCells(view.BoardTable);
        var colors = view.BoardPane.Style.RowColorGetter!;
        for (var index = 0; index < model.Board.Length; index++)
        {
            var color = colors(ColorArgs(view, index));
            if (index < 6) Assert.Null(color);
            else Assert.Equal(view.BoardPane.GetScheme().Disabled, color!.Normal);
        }
    }

    [Theory]
    [InlineData("High", true, 70)]
    [InlineData("Normal", false, 79)]
    public void Board_RemovedStateColumn_AllocatesFreedWidthToTitle(
        string priority, bool visible, int expectedTitleWidth)
    {
        var clock = new OwnerConsoleTestClock();
        var harness = new OwnerConsoleHarness(clock);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        OwnerConsoleBoardRow row = new("11111111", "Epic", new string('T', 200), "Active",
            "in progress: Developer", TimeSpan.FromMinutes(3), "11111111", Priority: priority);

        OwnerConsoleBoardRenderer.Render(view.BoardTable, view.BoardPane, [row], 120, 0);

        AssertColumns(view.BoardTable);
        Assert.Equal(visible, view.BoardPane.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal(expectedTitleWidth, view.BoardPane.Style.GetOrCreateColumnStyle(2).MaxWidth);
        var columns = Enumerable.Range(0, view.BoardTable.Columns.Count)
            .Where(index => view.BoardPane.Style.GetOrCreateColumnStyle(index).Visible).ToArray();
        Assert.Equal(120, columns.Sum(index => view.BoardPane.Style.GetOrCreateColumnStyle(index).MaxWidth)
            + columns.Length + 1);
    }

    private static OwnerConsoleHarness Fixture(OwnerConsoleTestClock clock, GoalStatus[] statuses)
    {
        var harness = new OwnerConsoleHarness(clock);
        foreach (var status in statuses) harness.AddGoal(Id(status), "Goal " + Id(status), AgentRole.Developer);
        var byId = statuses.ToDictionary(Id);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = byId[goal.Id],
            Tasks = goal.Tasks.Select(task => task with
            {
                Status = byId[goal.Id] == GoalStatus.Active ? WorkTaskStatus.Running : WorkTaskStatus.Pending
            }).ToArray(),
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "recorded reason",
                clock.Now.AddMinutes(byId[goal.Id] == GoalStatus.Parked ? -120 :
                    byId[goal.Id] == GoalStatus.Failed ? -60 : -10))]
        }).ToArray() });
        return harness;
    }

    private static string Id(GoalStatus status) => ((int)status + 1).ToString("D8");

    private static void AssertColumns(DataTable table) => Assert.Equal(
        new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" },
        table.Columns.Cast<DataColumn>().Select(column => column.ColumnName));

    private static void AssertNoEnumCells(DataTable table)
    {
        Assert.NotEmpty(table.Rows.Cast<DataRow>());
        foreach (DataRow row in table.Rows)
            foreach (var cell in row.ItemArray)
                Assert.DoesNotContain(Assert.IsType<string>(cell), Enum.GetNames<GoalStatus>());
    }

    // Invoke the real row styling hook without initializing a terminal driver.
    private static RowColorGetterArgs ColorArgs(OwnerConsoleFullScreenView view, int row) =>
        (RowColorGetterArgs)Activator.CreateInstance(typeof(RowColorGetterArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [view.BoardPane.Table!, row], null)!;

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness, TimeProvider clock,
        OwnerConsoleGateProgress? gate = null) => new(harness.State, harness.Questions, harness.Liveness,
            new Epics(), clock, gateProgress: _ => gate);

    private static OwnerConsoleFullScreenView View(IApplication app, OwnerConsoleHarness harness, TimeProvider clock) =>
        new(app, OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(), harness.State,
            harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock), () => Task.CompletedTask, clock: clock);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
