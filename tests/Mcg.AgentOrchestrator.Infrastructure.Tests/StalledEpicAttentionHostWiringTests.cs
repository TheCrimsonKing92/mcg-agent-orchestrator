using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixtures own all stores and logs; draft delegates never start workers or use Git.
public sealed class StalledEpicAttentionHostWiringTests
{
    [Fact]
    public async Task Shadow_tick_surfaces_stall_even_when_active_board_is_full()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = Policy() with { BoardFillTargetActiveGoals = 1 };
        h.Kernel.CreateGoal("Already active");
        var store = CollaborationItemStore.ForDirectory(h.Root);
        var calls = 0;
        var surface = new StalledEpicAttention(() => { calls++; return [Stalled()]; }, store, new(h.EventsPath));
        var host = Host(h, [], surface);
        try
        {
            Assert.True(h.Kernel.Goals.Count(goal => !goal.IsTerminal) >= h.Policy.BoardFillTargetActiveGoals);
            host.ServiceTick(h.Kernel);
            Assert.Equal(1, calls);
            Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.ListAsync()).Status);
            Assert.Null(host.CurrentRound);
            Assert.Empty(h.Store.ReadAll());
        }
        finally { host.Stop(); }
    }

    [Theory]
    [InlineData("off")]
    [InlineData("scoped")]
    [InlineData("shutdown")]
    public async Task Ineligible_tick_does_not_read_candidates_or_raise_items(string reason)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = Policy() with { BoardFillMode = reason == "off" ? ConductorBoardFillMode.Off : ConductorBoardFillMode.Shadow };
        var store = CollaborationItemStore.ForDirectory(h.Root);
        var calls = 0;
        var surface = new StalledEpicAttention(() => { calls++; return [Stalled()]; }, store, new(h.EventsPath));
        var host = Host(h, [], surface);
        try
        {
            if (reason == "shutdown") host.Stop();
            host.ServiceTick(h.Kernel, reason == "scoped" ? "scoped-goal" : null);
            Assert.Equal(0, calls);
            Assert.Empty(await store.ListAsync());
            Assert.Null(host.CurrentRound);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Stall_surface_preserves_drafted_identity_and_round_outcome()
    {
        using var baseline = new BoardFillTestHarness();
        using var observed = new BoardFillTestHarness();
        baseline.Policy = observed.Policy = Policy();
        observed.Items = baseline.Items;
        var baselineDrafted = new List<string>();
        var observedDrafted = new List<string>();
        var calls = 0;
        var store = CollaborationItemStore.ForDirectory(observed.Root);
        var surface = new StalledEpicAttention(() => { calls++; return [Stalled()]; }, store, new(observed.EventsPath));
        var baselineHost = Host(baseline, baselineDrafted);
        var observedHost = Host(observed, observedDrafted, surface);
        try
        {
            baselineHost.ServiceTick(baseline.Kernel);
            observedHost.ServiceTick(observed.Kernel);
            Assert.NotNull(baselineHost.CurrentRound);
            Assert.NotNull(observedHost.CurrentRound);
            await PanelTestHarness.Signal(baselineHost.CurrentRound!, "baseline draft finished");
            await PanelTestHarness.Signal(observedHost.CurrentRound!, "observed draft finished");
            Assert.Equal(new[] { Assert.Single(baseline.Items).Id }, baselineDrafted);
            Assert.Equal(baselineDrafted, observedDrafted);
            Assert.Equal(1, calls);
            Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.ListAsync()).Status);
        }
        finally { baselineHost.Stop(); observedHost.Stop(); }
        var before = Assert.Single(baseline.Store.ReadAll());
        var after = Assert.Single(observed.Store.ReadAll());
        Assert.Equal("draft", before.Outcome);
        Assert.Equal((before.BacklogItemId, before.Outcome, before.MainHead, before.DraftPath, before.ReceiptPath, before.Failure),
            (after.BacklogItemId, after.Outcome, after.MainHead, after.DraftPath, after.ReceiptPath, after.Failure));
        Assert.Equal(before.Checks.ToArray(), after.Checks.ToArray());
    }

    [Fact]
    public async Task Default_host_surfaces_remaining_scope_without_changing_source_stores()
    {
        using var f = new CliAuthorDraftCommandTests.Fixture();
        var epic = await new PortfolioStore(f.Workspace.PortfolioStorePath).AddEpicAsync("Control plane");
        await new EpicPlanStore(f.Workspace.PortfolioStorePath).AddSliceAsync(epic.Id, f.Item.Id);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Delivered slice");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, f.Item.Id, SourceBacklogCoverage.Slice);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            { Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray() });
        owner = Assert.Single(kernel.Goals);
        GoalOperationJournal.Completed(f.Workspace.ExecutionDirectory, owner, "conductor:land", "Landed slice fixture");
        _ = StateDbMigrations.EnsureUpToDate(f.Workspace.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(f.Workspace.SqliteStatePath).SaveAsync(kernel);
        var view = EpicPlanStatusReader.Load(f.Workspace, [epic])[epic.Id];
        var slice = Assert.Single(view.Items);
        Assert.Equal(EpicPlanSliceStatus.RemainingScope, slice.Status);
        Assert.Equal("Open: remaining scope", slice.Render());
        var portfolioBefore = File.ReadAllBytes(f.Workspace.PortfolioStorePath);
        var stateBefore = File.ReadAllBytes(f.Workspace.SqliteStatePath);
        var drafted = new List<string>();
        var host = ConductorBoardFillHost.CreateDefault(f.Workspace, Policy,
            () => CliAuthorDraftCommandTests.Fixture.MainSha, (id, _) =>
            {
                drafted.Add(id);
                return new("draft", 0, CliAuthorDraftCommandTests.Fixture.MainSha, null, null, []);
            });
        try
        {
            host.ServiceTick(kernel);
            var item = Assert.Single(await CollaborationItemStore.ForDirectory(f.Workspace.OrchestratorDirectory).ListAsync());
            Assert.Equal(CollaborationItemStatus.Raised, item.Status);
            Assert.Equal(CollaborationItemType.Decision, item.Type);
            Assert.Null(item.GoalId);
            Assert.StartsWith($"stalled-epic:{epic.Id}:", item.CorrelationKey);
            Assert.Contains($"1. {slice.Subject} — Open: remaining scope", item.Body);
            Assert.Empty(drafted);
            Assert.Null(host.CurrentRound);
            Assert.Equal(portfolioBefore, File.ReadAllBytes(f.Workspace.PortfolioStorePath));
            Assert.Equal(stateBefore, File.ReadAllBytes(f.Workspace.SqliteStatePath));
        }
        finally { host.Stop(); }
    }

    private static ConductorAutonomyPolicy Policy() => ConductorAutonomyPolicy.Permissive with
        { BoardFillMode = ConductorBoardFillMode.Shadow, BoardFillTargetActiveGoals = 10, BoardFillMaxDraftsPerDay = 10 };

    private static ConductorBoardFillHost Host(BoardFillTestHarness h, List<string> drafted,
        StalledEpicAttention? stalled = null) => new(h.Store, (id, _) =>
        {
            drafted.Add(id);
            return new("draft", 0, new string('c', 40), null, null, []);
        }, () => h.Items, (_, _) => _ => new(true, true, ""), () => h.Policy, new(h.EventsPath),
        () => h.Clock.UtcNow, mainHead: () => h.MainHead, trackedEdits: () => (true, null), stalledEpics: stalled);

    private static StalledEpicCandidate Stalled()
    {
        const string epicId = "12345678abcdef0123456789abcdef01";
        var item = new EpicPlanItemStatus(new(1, EpicPlanItemKind.Slice, "backlog", null, false),
            EpicPlanSliceStatus.RemainingScope, "Open slice");
        return new(epicId, "Control plane", new(new(epicId, null, [item.Item], []), [item], []));
    }
}
