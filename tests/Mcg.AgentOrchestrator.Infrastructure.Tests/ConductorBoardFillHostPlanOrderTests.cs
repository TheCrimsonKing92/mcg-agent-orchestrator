using System.Text.Json;
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
