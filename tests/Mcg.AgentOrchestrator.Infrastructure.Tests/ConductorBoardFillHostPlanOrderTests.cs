using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its workspace; the draft seam never starts a worker.
public sealed class ConductorBoardFillHostPlanOrderTests
{
    [Fact]
    public async Task Default_wiring_drafts_plan_slices_in_order_without_changing_plan_bytes()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var outside = await fixture.Store.UpdateAsync(fixture.Item.Id, new(Priority: "high"));
        var first = await fixture.Store.AddAsync("First slice");
        var second = await fixture.Store.AddAsync("Second slice");
        var epic = await new PortfolioStore(fixture.Workspace.PortfolioStorePath).AddEpicAsync("Plan");
        var plans = new EpicPlanStore(fixture.Workspace.PortfolioStorePath);
        await plans.AddSliceAsync(epic.Id, first.Id);
        await plans.AddSliceAsync(epic.Id, second.Id);
        var before = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
        var rows = (await plans.LoadAsync(epic.Id)).Items.ToArray();
        var kernel = new AgentOrchestratorKernel();
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(fixture.Workspace));
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("draft", 0, CliAuthorDraftCommandTests.Fixture.MainSha, null, null, []);
            });
        try
        {
            var items = BoardFillBacklogSnapshot.Read(fixture.Workspace.BacklogStorePath);
            Assert.Equal(outside.Id, BoardFillReadyItemSelector.Select(items, [], new HashSet<string>(), Ready)!.Id);
            host.ServiceTick(kernel);
            Assert.Equal(first.Id, Assert.Single(store.ReadAll()).BacklogItemId);
            await PanelTestHarness.Signal(host.CurrentRound!, "first plan draft finished");
            Assert.Equal(new[] { first.Id }, drafted);
            Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));

            host.ServiceTick(kernel);
            Assert.Equal(second.Id, Assert.Single(store.ReadAll().Where(round => round.Outcome is null)).BacklogItemId);
            await PanelTestHarness.Signal(host.CurrentRound!, "second plan draft finished");
            Assert.Equal(new[] { first.Id, second.Id }, drafted);
        }
        finally { host.Stop(); }
        Assert.Equal(rows, (await EpicPlanStore.OpenReadOnly(fixture.Workspace.PortfolioStorePath).LoadAsync(epic.Id)).Items.ToArray());
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
    }

    [Fact]
    public async Task Unreadable_plan_store_falls_back_and_records_one_failure_across_ticks()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new(Priority: "high"));
        await fixture.Store.AddAsync("Unplanned alternative");
        File.WriteAllText(fixture.Workspace.PortfolioStorePath, "not a SQLite database");
        var before = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
        var items = BoardFillBacklogSnapshot.Read(fixture.Workspace.BacklogStorePath);
        var expected = BoardFillReadyItemSelector.Select(items, [], new HashSet<string>(), Ready)!.Id;
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(fixture.Workspace));
        var kernel = new AgentOrchestratorKernel();
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("failed", 1, null, null, null, [], "stub draft failure");
            });
        try
        {
            host.ServiceTick(kernel);
            Assert.Equal(expected, Assert.Single(store.ReadAll()).BacklogItemId);
            await PanelTestHarness.Signal(host.CurrentRound!, "fallback draft finished");
            host.ServiceTick(kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "second fallback draft finished");
            Assert.Equal(new[] { expected, expected }, drafted);
            var failure = Assert.Single(PlanFailures(fixture.Workspace));
            Assert.StartsWith("BOARD_FILL_PLAN_READ_FAILED reason=SqliteException:", failure);
            Assert.DoesNotContain('\n', failure);
            Assert.DoesNotContain('\r', failure);
            Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Default_wiring_reads_a_plan_added_between_ticks()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        await fixture.Store.UpdateAsync(fixture.Item.Id, new(Priority: "high"));
        var slice = await fixture.Store.AddAsync("Later plan slice");
        var kernel = new AgentOrchestratorKernel();
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("failed", 1, null, null, null, [], "stub draft failure");
            });
        try
        {
            host.ServiceTick(kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "unplanned draft finished");
            var epic = await new PortfolioStore(fixture.Workspace.PortfolioStorePath).AddEpicAsync("New plan");
            await new EpicPlanStore(fixture.Workspace.PortfolioStorePath).AddSliceAsync(epic.Id, slice.Id);
            var before = File.ReadAllBytes(fixture.Workspace.PortfolioStorePath);
            host.ServiceTick(kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "new plan draft finished");
            Assert.Equal(new[] { fixture.Item.Id, slice.Id }, drafted);
            Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.PortfolioStorePath));
            Assert.Empty(PlanFailures(fixture.Workspace));
        }
        finally { host.Stop(); }
    }

    [Theory]
    [InlineData("terminal", false)]
    [InlineData("terminal", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    public async Task Claimed_sources_never_draft_and_independent_work_continues(string shape,
        bool unreadablePlan)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var source = await fixture.Store.UpdateAsync(fixture.Item.Id, new(Priority: "high"));
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Historical owner");
        // Persisted ownership is authoritative even without a source column association.
        if (shape is "legacy" or "ambiguous")
            kernel.SetGoalSourceBacklogItemLink(owner.Id, source.Id, SourceBacklogCoverage.Full);
        if (shape == "ambiguous")
        {
            var sibling = kernel.CreateGoal("Other historical owner");
            kernel.SetGoalSourceBacklogItemLink(sibling.Id, source.Id, SourceBacklogCoverage.Slice);
        }
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(goal => goal.Id == owner.Id.Value ? goal with { Status = GoalStatus.Completed } : goal).ToArray() });
        _ = StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        if (shape is not ("legacy" or "ambiguous"))
        {
            await repository.TransactAsync((current, _) => Task.FromResult((ShouldSave: true,
                Result: new SourceBacklogClaimStore(fixture.Workspace.SqliteStatePath)
                    .EnsureClaimForNewGoal(current, source.Id, owner.Id.Value, SourceBacklogCoverage.Full))));
            if (shape is "missing" or "malformed")
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    "Data Source=" + fixture.Workspace.SqliteStatePath + ";Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE source_backlog_claims SET owner_goal_id=$owner, coverage=$coverage WHERE backlog_item_id=$source";
                command.Parameters.AddWithValue("$owner", shape == "missing" ? "missing-owner" : owner.Id.Value);
                command.Parameters.AddWithValue("$coverage", shape == "malformed" ? "garbage" : "Full");
                command.Parameters.AddWithValue("$source", source.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
        }
        if (unreadablePlan) File.WriteAllText(fixture.Workspace.PortfolioStorePath, "not SQLite");
        if (shape == "malformed")
        {
            var dependent = await fixture.Store.AddAsync("Dependent with malformed prerequisite ownership");
            await fixture.Store.AddDependencyAsync(dependent.Id, new(source.Id, BacklogDependencyTargetKind.Backlog));
        }
        var facts = EpicPlanGoalClaimSnapshot.Read(fixture.Workspace.SqliteStatePath, kernel.Goals.ToArray()).Resolve(source);
        Assert.False(facts.AutomaticIntakeEligible);
        Assert.False(facts.ScopeComplete);
        var stateBefore = File.ReadAllBytes(fixture.Workspace.SqliteStatePath);
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("draft", 0, CliAuthorDraftCommandTests.Fixture.MainSha, null, null, []);
            });
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(fixture.Workspace));
        try
        {
            host.ServiceTick(kernel);
            Assert.Null(host.CurrentRound);
            Assert.Empty(store.ReadAll());
            Assert.Empty(drafted);
            var independent = await fixture.Store.AddAsync("Independent ready work");
            host.ServiceTick(kernel);
            Assert.Equal(independent.Id, Assert.Single(store.ReadAll()).BacklogItemId);
            await PanelTestHarness.Signal(host.CurrentRound!, "independent source draft finished");
            Assert.Equal(new[] { independent.Id }, drafted);
            Assert.Equal(stateBefore, File.ReadAllBytes(fixture.Workspace.SqliteStatePath));
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Open_landed_prerequisite_still_admits_an_unclaimed_dependent()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var dependent = await fixture.Store.AddAsync("Dependent work");
        await fixture.Store.AddDependencyAsync(dependent.Id, new(fixture.Item.Id, BacklogDependencyTargetKind.Backlog));
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Delivered prerequisite");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, fixture.Item.Id, SourceBacklogCoverage.Slice);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray() });
        owner = kernel.Goals.Single();
        GoalOperationJournal.Completed(fixture.Workspace.ExecutionDirectory, owner, "conductor:land", "Landed prerequisite fixture");
        _ = StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
        Assert.Equal(BacklogItemStatus.Open, (await fixture.Store.GetByIdPrefixAsync(fixture.Item.Id))!.Status);
        Assert.False(EpicPlanGoalClaimSnapshot.Read(fixture.Workspace.SqliteStatePath).Resolve(fixture.Item).ScopeComplete);
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("draft", 0, CliAuthorDraftCommandTests.Fixture.MainSha, null, null, []);
            });
        try
        {
            host.ServiceTick(kernel);
            await PanelTestHarness.Signal(host.CurrentRound!, "dependent source draft finished");
            Assert.Equal(new[] { dependent.Id }, drafted);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Unavailable_authoritative_claim_store_prevents_automatic_drafting()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        File.WriteAllText(fixture.Workspace.SqliteStatePath, "not SQLite");
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(fixture.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("draft", 0, CliAuthorDraftCommandTests.Fixture.MainSha, null, null, []);
            });
        try
        {
            host.ServiceTick(new AgentOrchestratorKernel());
            Assert.Null(host.CurrentRound);
            Assert.Empty(drafted);
        }
        finally { host.Stop(); }
    }
    private static ConductorAutonomyPolicy Policy() => ConductorAutonomyPolicy.Permissive with
    { BoardFillMode = ConductorBoardFillMode.Shadow, BoardFillTargetActiveGoals = 10, BoardFillMaxDraftsPerDay = 10 };

    private static BacklogReadiness Ready(BacklogItem item) =>
        BacklogDependencyReadiness.Evaluate(item, _ => null, _ => null, _ => new(null), _ => "Running");

    private static string[] PlanFailures(OrchestratorWorkspace workspace) =>
        File.ReadAllLines(workspace.ConductEventsLogPath).Select(line =>
        {
            using var entry = JsonDocument.Parse(line);
            return entry.RootElement.GetProperty("detail").GetString()!;
        }).Where(detail => detail.StartsWith("BOARD_FILL_PLAN_READ_FAILED", StringComparison.Ordinal)).ToArray();
}
