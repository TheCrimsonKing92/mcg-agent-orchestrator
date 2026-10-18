using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Views;

// Parallel-safe: all goals, console output, event observations and GUI instances are per test.
public sealed class OwnerConsolePriorityBadgeTests
{
    [Theory]
    [InlineData(GoalPriority.Critical, GoalPriority.Critical, false, "", "[C]")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, "", "[H]")]
    [InlineData(GoalPriority.Low, GoalPriority.Low, false, "", "[L]")]
    [InlineData(GoalPriority.Normal, GoalPriority.High, false, "", "[h]")]
    [InlineData(GoalPriority.Normal, GoalPriority.Critical, false, "", "[c]")]
    [InlineData(GoalPriority.High, GoalPriority.High, true, "", "[HS]")]
    [InlineData(GoalPriority.High, GoalPriority.High, false, "PRIORITY_DILUTION", "[H!]")]
    [InlineData(GoalPriority.High, GoalPriority.High, true, "PRIORITY_OVERLAP", "[HS!]")]
    [InlineData(GoalPriority.Normal, GoalPriority.Normal, true, "", "[S]")]
    [InlineData(GoalPriority.Normal, GoalPriority.Normal, false, "", "")]
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
        Assert.Equal(badge, Assert.Single(model.Gate!.Members).Priority);
        Assert.Equal("11111111" + (badge.Length == 0 ? "" : " " + badge) + " Readable title",
            OwnerConsoleGateFormatter.Lines(model.Gate)[1]);
        using var table = new DataTable();
        foreach (var column in new[] { "Goal", "Epic", "Title", "State", "Stage", "Age" }) table.Columns.Add(column);
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, model.Board, 0, 0);
        Assert.Equal((badge.Length == 0 ? "" : badge + " ") + "Readable title", table.Rows[0][2]);
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
        Assert.Equal("", Assert.Single(model.Board).Priority);
        events = [events[0] with { Detail = "PRIORITY_OVERLAP goal=11111111 other=22222222" }];
        model = builder.WithActivity(model, new(harness.Clock.GetUtcNow(), null, events, 0));
        Assert.Equal("[!]", Assert.Single(model.Board).Priority);
        model = builder.WithActivity(model, new(harness.Clock.GetUtcNow(), null, [], 0));
        Assert.Equal("", Assert.Single(model.Board).Priority);
    }

    [Fact]
    public void GoalDetailShowsEffectiveDonorAndSoloAfterStatusAndNormalOutputIsUnchanged()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.Kernel.CreateGoal(new GoalId("11111111-detail"), "Readable title", []);
        var normal = Render(goal);
        Assert.Equal(string.Join(Environment.NewLine, "11111111  Readable title", "Status: Draft",
            "Stage: Created", "Tasks:", "Recent events: none", ""), normal);
        goal = WithStoredPriority(harness.Kernel, goal, GoalPriority.High);
        GoalIsolationCommands.Apply(harness.Kernel, goal.Id, GoalIsolation.Solo, "test", "setup");
        Assert.Equal(normal.Replace("Status: Draft" + Environment.NewLine,
            "Status: Draft" + Environment.NewLine + "Priority: high (inherited from 22222222)" + Environment.NewLine +
            "Isolation: solo" + Environment.NewLine), Render(goal));
        var gate = Gate([new("11111111", "Readable title")]);
        Assert.Equal("11111111 Readable title", OwnerConsoleGateFormatter.Lines(gate)[1]);
        Assert.Equal("11111111 [H] Readable title", OwnerConsoleGateFormatter.Lines(gate with
            { Members = [new("11111111", "Readable title", "[H]")] })[1]);
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
