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
        // Intake links the goal to the REAL backlog item id (a GUID from AddAsync), not a title slug,
        // so auto-close fires for GUID-keyed items. Assert against the actually-seeded item's id.
        var seededId = new BacklogStore(workspace.BacklogStorePath)
            .ListAsync().GetAwaiter().GetResult()
            .Single(entry => string.Equals(entry.Title, "My Feature", StringComparison.Ordinal))
            .Id;
        Assert.Equal(seededId, currentGoal!.SourceBacklogItemId);
    }

    // ── Intake: batch (multiple filters -> one goal each) ─────────────────────

    [Xunit.Fact(DisplayName = "GoalBacklogLink_batch_intake_creates_one_linked_goal_per_filter")]
    public void BatchIntakeCreatesOneLinkedGoalPerFilter()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, "# Backlog\n\n## Alpha Feature\n\nAlpha body.\n\n## Beta Feature\n\nBeta body.\n");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CliCommandDispatcher.ExecuteCommand(
            ["backlog-intake", "Alpha Feature", "Beta Feature", "--create-simple-goal"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

        Assert.Equal(2, kernel.Goals.Count);
        var seeded = new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult();
        var alphaId = seeded.Single(entry => string.Equals(entry.Title, "Alpha Feature", StringComparison.Ordinal)).Id;
        var betaId = seeded.Single(entry => string.Equals(entry.Title, "Beta Feature", StringComparison.Ordinal)).Id;
        Assert.True(kernel.Goals.Any(goal => goal.SourceBacklogItemId == alphaId));
        Assert.True(kernel.Goals.Any(goal => goal.SourceBacklogItemId == betaId));
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

        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath);

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
        GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath);

        var fetched = await store.GetByExactIdAsync(item.Id);
        Assert.True(fetched is not null);
        Assert.True(fetched!.Status == BacklogItemStatus.Done);
    }

    [Xunit.Fact(DisplayName = "GoalBacklogLink_land_without_source_backlog_item_warns_and_continues")]
    public void LandWithoutSourceBacklogItemWarnsAndContinues()
    {
        var dbPath = Path.Combine(CreateTempDirectory(), "backlog.db");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Land this", [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer)]);
        var messages = new List<string>();

        var closed = GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, dbPath, messages.Add);

        Assert.False(closed);
        Assert.Contains(messages, message => message.Contains("without a linked source backlog item", StringComparison.OrdinalIgnoreCase));
    }
}
