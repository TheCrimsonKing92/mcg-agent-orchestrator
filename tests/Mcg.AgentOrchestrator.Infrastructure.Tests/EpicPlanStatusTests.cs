using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: real stores are isolated per test; goal state is saved through the conductor's repository seam.
public sealed class EpicPlanStatusTests : CliCommandTestBase
{
    private readonly string root = CreateTempDirectory();

    [Fact]
    public async Task Plan_RecomputesReadyInFlightVerifiedAndLanded_WithoutPersistingSliceStatus()
    {
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Plan");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var slices = new[] { await backlog.AddAsync("First"), await backlog.AddAsync("Second"), await backlog.AddAsync("Third") };
        foreach (var slice in slices)
            Execute(workspace, "epic-plan-add", epic.Id, "--backlog", slice.Id);
        var file = Path.Combine(root, "step.txt");
        File.WriteAllText(file, "Read the probe");
        Execute(workspace, "epic-plan-add", epic.Id, "--step-file", file);
        Assert.Equal(4, (await EpicPlanStore.OpenReadOnly(workspace.PortfolioStorePath).LoadAsync(epic.Id)).Items.Count);
        var before = File.ReadAllBytes(workspace.PortfolioStorePath);
        AssertNoStatusColumn(workspace.PortfolioStorePath);
        foreach (var slice in slices) AssertStatus(workspace, epic.Id, slice, "Ready");
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        await backlog.AddDependencyAsync(slices[1].Id, new(slices[0].Id, BacklogDependencyTargetKind.Backlog));
        AssertStatus(workspace, epic.Id, slices[1], $"Blocked (blocked on {slices[0].Id})");
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));

        var kernel = new AgentOrchestratorKernel();
        var ids = new List<GoalId>();
        foreach (var slice in slices)
        {
            var goal = kernel.CreateGoal(new GoalId(Guid.NewGuid().ToString("n")), slice.Title);
            kernel.SetGoalSourceBacklogItemId(goal.Id, slice.Id);
            await portfolio.AssignGoalToEpicAsync(goal.Id.Value, epic.Id);
            ids.Add(goal.Id);
        }
        // The assignments precede the baseline; reads thereafter must leave every portfolio byte unchanged.
        before = File.ReadAllBytes(workspace.PortfolioStorePath);
        foreach (var state in new[] { GoalStatus.Active, GoalStatus.Verified, GoalStatus.Completed })
        {
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            { Goals = snapshot.Goals.Select(goal => goal with { Status = state }).ToArray() });
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            for (var index = 0; index < slices.Length; index++)
                AssertStatus(workspace, epic.Id, slices[index], state switch
                {
                    GoalStatus.Active => $"In flight ({ids[index].Value[..8]} Active)",
                    GoalStatus.Verified => "Verified", _ => "Landed"
                });
            Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
            AssertNoStatusColumn(workspace.PortfolioStorePath);
            var output = Execute(workspace, "epic-plan", epic.Id);
            var notInPlan = output[(output.IndexOf("Not in plan:", StringComparison.Ordinal) + "Not in plan:".Length)..];
            Assert.DoesNotContain("Goal:", notInPlan);
        }
        await backlog.CloseAsync(slices[0].Id);
        AssertStatus(workspace, epic.Id, slices[1], "Landed");
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        Assert.Contains("3 of 3 slices landed; next: 4. Read the probe — open", Execute(workspace, "epic-list"));
    }

    [Fact]
    public async Task SupersededBacklog_TakesPrecedenceOverItsActiveGoal_AndIsComplete()
    {
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Plan");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var old = await backlog.AddAsync("Old slice");
        var replacement = await backlog.AddAsync("Replacement");
        Execute(workspace, "epic-plan-add", epic.Id, "--backlog", old.Id);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(Guid.NewGuid().ToString("n")), "Old goal");
        kernel.SetGoalSourceBacklogItemId(goal.Id, old.Id);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(row => row with { Status = GoalStatus.Active }).ToArray() });
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        AssertStatus(workspace, epic.Id, old, $"In flight ({goal.Id.Value[..8]} Active)");
        await backlog.SupersedeAsync(old.Id, replacement.Id);
        Assert.Equal(BacklogItemStatus.Superseded, (await backlog.GetByIdPrefixAsync(old.Id))!.Status);
        AssertStatus(workspace, epic.Id, old, "Superseded");
        Assert.Contains("Next step: complete", Execute(workspace, "epic-plan", epic.Id));
        Assert.DoesNotContain("Stalled", Execute(workspace, "epic-plan", epic.Id));
    }

    [Theory]
    [InlineData("Failed", "Failed (recorded reason)")]
    [InlineData("AcceptanceFailed", "Failed (recorded reason)")]
    [InlineData("Parked", "Parked (Goal parked: recorded reason)")]
    [InlineData("Cancelled", "Ready")]
    [InlineData("Superseded", "Ready")]
    public async Task GoalOutcomes_UseRecordedReasons_AndClosedGoalsDoNotSupersedeBacklog(string state, string expected)
    {
        var workspace = CreateRefinedWorkspace(root);
        var epic = await new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Plan");
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Slice");
        Execute(workspace, "epic-plan-add", epic.Id, "--backlog", item.Id);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(Guid.NewGuid().ToString("n")), "Claimed goal");
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        kernel.RecordGoalPolicyDecision(goal.Id, "recorded reason");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(row => row with
            {
                Status = Enum.Parse<GoalStatus>(state),
                Timeline = state == "Parked" ? row.Timeline.Concat(new[]
                {
                    new ProgressEventSnapshot(row.Id, null, ProgressKind.TaskFailed, "stale task failure",
                        new DateTimeOffset(2026, 10, 8, 1, 0, 0, TimeSpan.Zero)),
                    new ProgressEventSnapshot(row.Id, null, ProgressKind.GoalPolicyDecision, "Goal parked: older reason",
                        new DateTimeOffset(2026, 10, 8, 2, 0, 0, TimeSpan.Zero)),
                    new ProgressEventSnapshot(row.Id, null, ProgressKind.GoalPolicyDecision, "Goal parked: recorded reason",
                        new DateTimeOffset(2026, 10, 8, 3, 0, 0, TimeSpan.Zero)),
                    new ProgressEventSnapshot(row.Id, null, ProgressKind.GoalPolicyDecision, "unrelated policy decision",
                        new DateTimeOffset(2026, 10, 8, 4, 0, 0, TimeSpan.Zero))
                }).ToArray() : row.Timeline
            }).ToArray() });
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        AssertStatus(workspace, epic.Id, item, expected);
    }

    [Fact]
    public async Task ClosedWithoutClaim_IsDone_AndMissingMemberIsVisible()
    {
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Plan");
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Manual work");
        Execute(workspace, "epic-plan-add", epic.Id, "--backlog", item.Id);
        await backlog.CloseAsync(item.Id);
        await portfolio.AssignGoalToEpicAsync("missing-goal", epic.Id);
        AssertStatus(workspace, epic.Id, item, "Done");
        Assert.Contains("Goal: missing-goal", Execute(workspace, "epic-plan", epic.Id));
        Assert.Contains("1 of 1 slices landed; next: complete", Execute(workspace, "epic-show", epic.Id));
    }

    private static void AssertStatus(OrchestratorWorkspace workspace, string epicId, BacklogItem item, string status) =>
        Assert.Contains($"{item.Id[..8]} {item.Title} — {status}{Environment.NewLine}", Execute(workspace, "epic-plan", epicId));
    private static string Execute(OrchestratorWorkspace workspace, params string[] args) =>
        ExecuteCliAndCapture(args, new AgentOrchestratorKernel(), workspace);
    private static void AssertNoStatusColumn(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(epic_plan_items)";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        Assert.Contains("done", columns);
        Assert.DoesNotContain("status", columns);
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(root, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
