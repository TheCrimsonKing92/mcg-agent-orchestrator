using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Views;

// Parallel-safe: all goals, console output, event observations and GUI instances are per test.
public sealed class OwnerConsolePriorityBadgeTests
{
    [Theory]
    [InlineData(GoalPriority.Critical, GoalPriority.Critical, false, "", "Critical")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, "", "High")]
    [InlineData(GoalPriority.Low, GoalPriority.Low, false, "", "Low")]
    [InlineData(GoalPriority.Normal, GoalPriority.High, false, "", "High")]
    [InlineData(GoalPriority.Normal, GoalPriority.Critical, false, "", "Critical")]
    [InlineData(GoalPriority.High, GoalPriority.High, true, "", "High")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, "PRIORITY_DILUTION", "High")]
    [InlineData(GoalPriority.High, GoalPriority.High, true, "PRIORITY_OVERLAP", "High")]
    [InlineData(GoalPriority.Normal, GoalPriority.Normal, true, "", "Normal")]
    [InlineData(GoalPriority.Normal, GoalPriority.Normal, false, "", "Normal")]
    public async Task BuilderDecoratesBoardAndGateFromOwnInheritedIsolationAndRecentWarnings(
        GoalPriority own, GoalPriority effective, bool solo, string warning, string badge)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111-badge", "Readable title", AgentRole.Developer);
        harness.Kernel.SetGoalPriority(goal.Id, own, "test", "setup");
        if (effective != own) goal = WithStoredPriority(harness.Kernel, goal, effective);
        if (solo) GoalIsolationCommands.Apply(harness.Kernel, goal.Id, GoalIsolation.Solo, "test", "setup");
        var gate = Gate([new(goal.Id.Value[..8], "Readable title")]);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock,
            gateProgress: _ => gate);
        OwnerConductEvent[] events = warning.Length == 0 ? [] :
            [new(harness.Clock.GetUtcNow(), "priority", goal.Id.Value[..8], warning + " goal=11111111 other=33333333")];
        var model = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, events, 0));
        var row = Assert.Single(model.Board);
        Assert.Equal(badge, row.Priority);
        Assert.Equal("Readable title", row.Title);
        Assert.Equal("11111111 Readable title",
            OwnerConsoleGateFormatter.Lines(model.Gate)[1]);
        using var table = new DataTable();
        foreach (var column in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" }) table.Columns.Add(column);
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, model.Board, 0, 0);
        Assert.Equal("Readable title", table.Rows[0][2]);
        Assert.Equal(badge, table.Rows[0][3]);
    }

    [Fact]
    public async Task WarningOtherFieldDoesNotMarkTheOtherGoalAndOldWarningDisappears()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111-badge", "Normal", AgentRole.Developer);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        var events = new[] { new OwnerConductEvent(harness.Clock.GetUtcNow(), "priority", "22222222",
            "PRIORITY_OVERLAP goal=22222222 other=11111111") };
        var model = await builder.BuildAsync(new(harness.Clock.GetUtcNow(), null, events, 0));
        Assert.Equal("Normal", Assert.Single(model.Board).Priority);
        Assert.Null(Assert.Single(model.Board).PriorityWarning);
        events = [events[0] with { Detail = "PRIORITY_OVERLAP goal=11111111 other=22222222" }];
        model = builder.WithActivity(model, new(harness.Clock.GetUtcNow(), null, events, 0));
        Assert.Equal("Normal", Assert.Single(model.Board).Priority);
        Assert.Equal("PRIORITY_OVERLAP goal=11111111 other=22222222", Assert.Single(model.Board).PriorityWarning);
        model = builder.WithActivity(model, new(harness.Clock.GetUtcNow(), null, [], 0));
        Assert.Equal("Normal", Assert.Single(model.Board).Priority);
        Assert.Null(Assert.Single(model.Board).PriorityWarning);
    }

    [Fact]
    public void GoalDetailShowsEffectiveDonorAndSoloAfterStatusAndNormalOutputIsUnchanged()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-detail"), "Readable title",
            [new(TaskId.New(), "Build", AgentRole.Developer)]);
        var normal = Render(goal);
        Assert.Equal(string.Join(Environment.NewLine, "11111111  Readable title", "Status: draft",
            "Stage: Created", "Role: Developer", "Tasks:", "  Developer: Pending", "Recent events: none", ""), normal);
        goal = WithStoredPriority(harness.Kernel, goal, GoalPriority.High);
        GoalIsolationCommands.Apply(harness.Kernel, goal.Id, GoalIsolation.Solo, "test", "setup");
        Assert.Equal(normal.Replace("Status: draft" + Environment.NewLine,
            "Status: draft" + Environment.NewLine + "Priority: High (inherited from dependent goal 22222222; not set with goal-priority)" + Environment.NewLine +
            "Isolation: solo" + Environment.NewLine), Render(goal));
        var gate = Gate([new("11111111", "Readable title")]);
        Assert.Equal("11111111 Readable title", OwnerConsoleGateFormatter.Lines(gate)[1]);
    }

    private static string Render(Goal goal)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal, null, []), output, TimeZoneInfo.Utc);
        return output.Text;
    }
    private static Goal WithStoredPriority(AgentOrchestratorKernel kernel, Goal goal, GoalPriority level)
    {
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value
            ? item with { PriorityScheduling = new(GoalPriorities.Format(level), InheritedFromGoalId: "22222222-donor") }
            : item).ToArray() });
        return kernel.GetGoal(goal.Id);
    }
    private static OwnerConsoleGateProgress Gate(IReadOnlyList<OwnerConsoleGateMember> members) =>
        new(true, "solo", members, DateTimeOffset.UnixEpoch, TimeSpan.Zero, [], 0, 0, 0, 0);
    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("Epic");
    }
}
