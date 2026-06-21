using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalBacklogLinkTests
{
    // ── Intake: create with link ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_create_with_link_carries_source_backlog_item_id")]
    public void CreateWithLinkCarriesSourceBacklogItemId()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## My Feature\n\nFeature body.\n");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "My Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.True(currentGoal is not null);
        var expectedId = BacklogStore.SlugId("My Feature");
        Assert.Equal(expectedId, currentGoal!.SourceBacklogItemId);
    }

    // ── Intake: skip already-done item ───────────────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_no_goal_for_already_done_backlog_item_at_intake")]
    public async Task NoGoalForAlreadyDoneItemAtIntake()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);

        // Seed the item into the BacklogStore and mark it Done.
        var store = new BacklogStore(workspace.BacklogStorePath);
        var itemId = BacklogStore.SlugId("Done Feature");
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new BacklogItem(itemId, "Done Feature", "Feature body.", BacklogItemStatus.Open, now, now, null));
        await store.CloseAsync(itemId);

        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Done Feature", "--create-simple-goal"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        });

        Assert.True(currentGoal is null);
        Assert.Equal(0, kernel.Goals.Count);
        Assert.True(output.Contains("already Done", StringComparison.OrdinalIgnoreCase));
    }

    // ── Land: close linked item on successful acceptance ─────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_closes_linked_backlog_item")]
    public async Task LandClosesLinkedBacklogItem()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var store = new BacklogStore(dbPath);
        var item = await store.AddAsync("Landing target");

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);

        CliCommandHandlers.AutoCloseSourceBacklogItem(goal, dbPath);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
    }

    // ── Land: already-closed item is a safe no-op ────────────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_is_noop_when_backlog_item_already_closed")]
    public async Task LandIsNoopWhenItemAlreadyClosed()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var store = new BacklogStore(dbPath);
        var item = await store.AddAsync("Already shipped");
        await store.CloseAsync(item.Id);

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);

        // Must not throw; must leave item as Done.
        CliCommandHandlers.AutoCloseSourceBacklogItem(goal, dbPath);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
    }
}
