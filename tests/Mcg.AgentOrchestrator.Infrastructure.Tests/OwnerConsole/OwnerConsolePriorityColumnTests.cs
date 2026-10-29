using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

// Parallel-safe: kernel snapshots, event lists, output and uninitialized GUI instances are per test.
public sealed class OwnerConsolePriorityColumnTests
{
    private const string Overlap = "PRIORITY_OVERLAP goal=11111111 other=33333333 kind=File path=config/acceptance-manifest.json";

    [Theory]
    [InlineData(GoalPriority.Critical, GoalPriority.Critical, false, false, "Critical")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, false, "High")]
    [InlineData(GoalPriority.Low, GoalPriority.Low, false, false, "Low")]
    [InlineData(GoalPriority.Normal, GoalPriority.High, false, false, "High")]
    [InlineData(GoalPriority.Normal, GoalPriority.Critical, false, false, "Critical")]
    [InlineData(GoalPriority.High, GoalPriority.High, true, false, "High")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, true, "High")]
    public async Task BoardPriorityCellAndTitleCellPerPriorityKind(
        GoalPriority own, GoalPriority stored, bool solo, bool warning, string expected)
    {
        var (harness, _, builder) = Fixture(own, stored, solo);
        OwnerConductEvent[] events = warning ? [new(harness.Clock.GetUtcNow(), "priority", "11111111", Overlap)] : [];
        var model = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, events, 0));
        Assert.Equal(expected, Assert.Single(model.Board).Priority);
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, model.Board, 0, 0);
        var priority = Assert.IsType<string>(table.Rows[0]["PRIORITY"]);
        var title = Assert.IsType<string>(table.Rows[0]["TITLE"]);
        Assert.Equal(expected, priority);
        Assert.Equal("Readable title", title);
        Assert.False(title.StartsWith("[", StringComparison.Ordinal));
        Assert.DoesNotContain("(", priority);
        Assert.DoesNotContain("inh", priority);
        Assert.Equal("11111111 Readable title", OwnerConsoleGateFormatter.Lines(model.Gate!)[1]);
    }

    [Fact]
    public async Task NormalGoalShowsNormalAndPlainTitle()
    {
        var (harness, goal, builder) = Fixture(GoalPriority.Normal, GoalPriority.Normal, false);
        var normal = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
        Assert.Equal("Normal", Assert.Single(normal.Board).Priority);
        Assert.Null(Assert.Single(normal.Board).PriorityWarning);
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, normal.Board, 0, 0);
        Assert.Equal("Normal", table.Rows[0]["PRIORITY"]);
        var normalTitle = Assert.IsType<string>(table.Rows[0]["TITLE"]);
        Assert.Equal("Readable title", normalTitle);

        harness.Kernel.SetGoalPriority(goal.Id, GoalPriority.High, "test", "setup");
        GoalIsolationCommands.Apply(harness.Kernel, goal.Id, GoalIsolation.Solo, "test", "setup");
        var raised = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null,
            [new(harness.Clock.GetUtcNow(), "priority", "11111111", Overlap)], 0));
        Assert.Equal("High", Assert.Single(raised.Board).Priority);
        Assert.Equal(Overlap, Assert.Single(raised.Board).PriorityWarning);
        OwnerConsoleBoardRenderer.Render(table, view, raised.Board, 0, 0);
        Assert.Equal(normalTitle, table.Rows[0]["TITLE"]);
    }

    [Fact]
    public async Task WarningGoesToDetailNotBoard()
    {
        var (harness, goal, builder) = Fixture(GoalPriority.Normal, GoalPriority.High, true);
        var plain = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
        var overlap = new OwnerConductEvent(harness.Clock.GetUtcNow(), "priority", "11111111", Overlap);
        var warned = builder.WithActivity(plain, new(harness.Clock.GetUtcNow(), null, [overlap], 0));
        Assert.Equal(overlap.Detail, Assert.Single(warned.Board).PriorityWarning);
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, plain.Board, 0, 0);
        var priority = table.Rows[0]["PRIORITY"];
        var title = table.Rows[0]["TITLE"];
        OwnerConsoleBoardRenderer.Render(table, view, warned.Board, 0, 0);
        Assert.Equal(priority, table.Rows[0]["PRIORITY"]);
        Assert.Equal(title, table.Rows[0]["TITLE"]);

        var older = overlap with { Timestamp = overlap.Timestamp.AddMinutes(-1), Detail = "PRIORITY_DILUTION goal=11111111 epic=44444444" };
        var multiple = builder.WithActivity(plain, new(harness.Clock.GetUtcNow(), null, [older, overlap], 0));
        Assert.Equal(overlap.Detail + " (+1 more)", Assert.Single(multiple.Board).PriorityWarning);
        var other = overlap with { Detail = "PRIORITY_OVERLAP goal=22222222 other=11111111" };
        Assert.Null(Assert.Single(builder.WithActivity(plain,
            new(harness.Clock.GetUtcNow(), null, [other], 0)).Board).PriorityWarning);
        var tied = older with { Timestamp = overlap.Timestamp };
        Assert.Equal(tied.Detail + " (+1 more)", OwnerConsolePriorityText.Warning(goal, [tied, overlap]));

        var detail = Render(new(goal, null, [], PriorityWarning: Assert.Single(warned.Board).PriorityWarning));
        Assert.Contains(string.Join(Environment.NewLine,
            "Priority: High (inherited from dependent goal 22222222; not set with goal-priority)",
            "Priority warning: " + overlap.Detail, "Isolation: solo"), detail);
        Assert.Equal(Render(new(goal, null, [])), Render(new(goal, null, [], PriorityWarning: null)));
        var composed = new OwnerConsoleHarness.FakeOutput();
        await OwnerConsoleGoalDetailFormatter.ComposeAsync(new OwnerGoalDetailQuery(harness.State, harness.Tail),
            goal.Id.Value, composed, CancellationToken.None, TimeZoneInfo.Utc, priorityWarning: overlap.Detail);
        Assert.Contains("Priority warning: " + overlap.Detail, composed.Text);
    }

    [Fact]
    public void NarrowPaneHidesPriorityWithoutOverflow()
    {
        OwnerConsoleBoardRow row = new("11111111", "Owner control plane", new string('T', 100),
            "Running", new string('S', 30), TimeSpan.FromMinutes(5), "11111111-goal", Priority: "Critical");
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, [row], 40, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
        var otherWidths = new[] { 0, 1, 4, 5, 6 }.Sum(column => view.Style.GetOrCreateColumnStyle(column).MaxWidth);
        Assert.Equal(40 - otherWidths - 7, view.Style.GetOrCreateColumnStyle(2).MaxWidth);
        Assert.True(VisibleWidths(view, table) + 7 <= 40);
        Assert.Equal("Critical", table.Rows[0]["PRIORITY"]);
        AssertCellsFit(table, view);

        OwnerConsoleBoardRenderer.Render(table, view, [row], 120, 0);
        Assert.True(view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal(8, view.Style.GetOrCreateColumnStyle(3).MaxWidth);
        Assert.True(view.Style.GetOrCreateColumnStyle(2).MaxWidth >= 16);
        Assert.Equal(120, VisibleWidths(view, table) + 8);
        AssertCellsFit(table, view);

        row = row with { Epic = "Epic", State = "Running", Stage = "Developer", Title = "Goal 00", Priority = "High" };
        OwnerConsoleBoardRenderer.Render(table, view, [row], 72, 0);
        Assert.True(view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal("Goal 00", table.Rows[0]["TITLE"]);
        AssertCellsFit(table, view);

        OwnerConsoleBoardRenderer.Render(table, view, [row with { Title = new string('T', 100) }], 40, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
        OwnerConsoleBoardRenderer.Render(table, view, [row], 0, 0);
        Assert.True(view.Style.GetOrCreateColumnStyle(3).Visible);
        OwnerConsoleBoardRenderer.Render(table, view, [], 40, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(120)]
    public void AllNormalRowsHidePriorityAndMixedRowsRestoreIt(int width)
    {
        OwnerConsoleBoardRow first = new("11111111", "Epic", "First goal", "Active", "Developer", null,
            "11111111-first", Priority: "Normal");
        var second = first with { GoalPrefix = "22222222", GoalId = "22222222-second", Title = "Second goal" };
        using var table = BoardTable();
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, [first, second], width, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal(2, table.Rows.Count);
        Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal("Normal", row["PRIORITY"]));

        OwnerConsoleBoardRenderer.Render(table, view, [first, second with { Priority = "High" }], width, 0);
        Assert.Equal(width != 40, view.Style.GetOrCreateColumnStyle(3).Visible);
        Assert.Equal("Normal", table.Rows[0]["PRIORITY"]);
        Assert.Equal("High", table.Rows[1]["PRIORITY"]);

        OwnerConsoleBoardRenderer.Render(table, view, [first, second], width, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
        OwnerConsoleBoardRenderer.Render(table, view, [], width, 0);
        Assert.False(view.Style.GetOrCreateColumnStyle(3).Visible);
    }

    [Theory]
    [InlineData(GoalPriority.Normal, GoalPriority.High, "22222222-donor", "High",
        "Priority: High (inherited from dependent goal 22222222; not set with goal-priority)")]
    [InlineData(GoalPriority.Normal, GoalPriority.Critical, null, "Critical",
        "Priority: Critical (inherited from the goal's stored level; not set with goal-priority)")]
    [InlineData(GoalPriority.High, GoalPriority.Critical, "22222222-donor", "Critical",
        "Priority: Critical (inherited from dependent goal 22222222; goal-priority set High)")]
    public void DetailPriorityLineSaysWhereTheLevelCameFrom(
        GoalPriority own, GoalPriority stored, string? donor, string word, string expected)
    {
        var (_, goal, _) = Fixture(own, stored, false, donor);
        Assert.Equal(expected, PriorityLine(Render(new(goal, null, []))));
        Assert.Equal(word, OwnerConsolePriorityText.Column(goal));
    }

    [Fact]
    public void ExplicitHighDetailHasNoInheritedText()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-detail"), "Readable title",
            [new(TaskId.New(), "Build", AgentRole.Developer)]);
        var normal = Render(new(goal, null, []));
        Assert.DoesNotContain(normal.Split(Environment.NewLine), line => line.StartsWith("Priority", StringComparison.Ordinal));
        Assert.Equal(string.Join(Environment.NewLine, "11111111  Readable title", "Status: Draft",
            "Stage: Created", "Role: Developer", "Tasks:", "  Developer: Pending", "Recent events: none", ""), normal);

        harness.Kernel.SetGoalPriority(goal.Id, GoalPriority.High, "test", "setup");
        var priority = PriorityLine(Render(new(goal, null, [])));
        Assert.Equal("Priority: High (set with goal-priority)", priority);
        Assert.DoesNotContain("inherited", priority);
    }

    private static (OwnerConsoleHarness Harness, Goal Goal, OwnerConsoleViewModelBuilder Builder) Fixture(
        GoalPriority own, GoalPriority stored, bool solo, string? donor = "22222222-donor")
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111-column", "Readable title", AgentRole.Developer);
        harness.Kernel.SetGoalPriority(goal.Id, own, "test", "setup");
        if (stored != own) goal = WithStoredPriority(harness.Kernel, goal, stored, donor);
        if (solo) GoalIsolationCommands.Apply(harness.Kernel, goal.Id, GoalIsolation.Solo, "test", "setup");
        OwnerConsoleGateProgress gate = new(true, "solo", [new("11111111", "Readable title")],
            DateTimeOffset.UnixEpoch, TimeSpan.Zero, [], 0, 0, 0, 0);
        return (harness, goal, new(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock,
            gateProgress: _ => gate));
    }

    private static Goal WithStoredPriority(AgentOrchestratorKernel kernel, Goal goal, GoalPriority level, string? donor)
    {
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value
            ? item with { PriorityScheduling = new(GoalPriorities.Format(level), InheritedFromGoalId: donor) }
            : item).ToArray() });
        return kernel.GetGoal(goal.Id);
    }

    private static DataTable BoardTable()
    {
        var table = new DataTable();
        foreach (var column in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATE", "STAGE", "AGE" }) table.Columns.Add(column);
        return table;
    }

    private static int VisibleWidths(TableView view, DataTable table) => Enumerable.Range(0, table.Columns.Count)
        .Where(column => view.Style.GetOrCreateColumnStyle(column).Visible)
        .Sum(column => view.Style.GetOrCreateColumnStyle(column).MaxWidth);

    private static void AssertCellsFit(DataTable table, TableView view)
    {
        foreach (DataRow row in table.Rows)
            for (var column = 0; column < table.Columns.Count; column++)
                Assert.True(((string)row[column]).GetColumns() <= view.Style.GetOrCreateColumnStyle(column).MaxWidth,
                    $"Cell {table.Columns[column].ColumnName} exceeds its MaxWidth.");
    }

    private static string Render(OwnerGoalDetailSnapshot detail)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail, output, TimeZoneInfo.Utc);
        return output.Text;
    }

    private static string PriorityLine(string text) => Assert.Single(text.Split(Environment.NewLine),
        line => line.StartsWith("Priority: ", StringComparison.Ordinal));

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("Epic");
    }
}
