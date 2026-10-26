using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ApplicationLayerHelperEquivalenceTests : IDisposable
{
    // Parallel-safe: every test owns a unique root and SQLite store, with pooling disabled by BacklogStore.
    private readonly string root = Path.Combine(Path.GetTempPath(), "mcg-tests", Guid.NewGuid().ToString("N"));

    public ApplicationLayerHelperEquivalenceTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("seeded-item", "Friendly title")]
    [InlineData("no-source-id", null)]
    [InlineData("missing-store", null)]
    [InlineData("absent-id", null)]
    public async Task Friendly_label_Store_inputs_Matches_cli_and_expected_title(
        string scenario,
        string? expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Resolve friendly label");
        var path = Path.Combine(root, "backlog.db");

        if (scenario == "missing-store")
        {
            kernel.SetGoalSourceBacklogItemId(goal.Id, Guid.NewGuid().ToString("N"));
            Assert.False(File.Exists(path));
        }
        else
        {
            var store = new BacklogStore(path);
            var item = await store.AddAsync("Friendly title");
            Assert.True(File.Exists(path));
            Assert.Equal("Friendly title", (await store.GetByExactIdAsync(item.Id))?.Title);
            if (scenario == "seeded-item")
                kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
            else if (scenario == "absent-id")
            {
                kernel.SetGoalSourceBacklogItemId(goal.Id, Guid.NewGuid().ToString("N"));
                Assert.Null(await store.GetByExactIdAsync(goal.SourceBacklogItemId!));
            }
            else
                Assert.Null(goal.SourceBacklogItemId);
        }

        var label = GoalFriendlyLabel.Resolve(goal, path);

        Assert.Equal(expected, label);
        Assert.Equal(CliCommandHandlers.ResolveGoalFriendlyLabel(goal, path), label);
    }

    [Theory]
    [InlineData("developer", AgentRole.Developer)]
    [InlineData("REVIEWER", AgentRole.Reviewer)]
    public void Role_parser_Mixed_case_Returns_expected_role(string value, AgentRole expected)
    {
        Assert.Equal(expected, AgentRoleParser.Parse(value));
    }

    [Fact]
    public void Role_parser_Invalid_role_Preserves_message_across_entry_points()
    {
        var helperError = Assert.Throws<ArgumentException>(() => AgentRoleParser.Parse("nope"));
        var cliError = Assert.Throws<ArgumentException>(() => CliArgumentParser.ParseAgentRole("nope"));
        var factoryError = Assert.Throws<ArgumentException>(() =>
            AgentDefinitionFactory.Create(new AgentDefinitionInput("nope", "codex", "gpt")));

        Assert.StartsWith("Role must be one of: ", helperError.Message);
        Assert.Equal(cliError.Message, helperError.Message);
        Assert.Equal(factoryError.Message, helperError.Message);
    }

    [Fact]
    public void Lifecycle_reader_No_worktree_or_journal_Matches_cli_facts()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goal = new AgentOrchestratorKernel().CreateGoal("Read lifecycle facts");
        Assert.Null(GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id));
        Assert.Empty(GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id).LatestByOperation);

        var facts = GoalLifecycleFactsReader.Read(workspace, goal);

        Assert.False(facts.WorkspaceExists);
        Assert.Equal(GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal), facts);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
