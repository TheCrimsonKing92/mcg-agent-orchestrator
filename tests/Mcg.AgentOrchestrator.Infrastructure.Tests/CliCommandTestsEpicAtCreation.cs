using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every database, input file and child working directory is test-owned.
// Console capture uses the existing CliCommandTestBase capture mechanism.
public sealed class CliCommandTestsEpicAtCreation : CliCommandTestBase
{
    [Theory]
    [InlineData("--text-file")]
    [InlineData("--brief-file")]
    public void Goal_FileAndEpicTitle_CommitsBeforeAssigning(string fileFlag)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Creation Board").GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();
        var file = WriteObjective(workspace);

        var output = CreateCommittedGoal(
            ["goal", fileFlag, file, "--epic", epic.Title], kernel, workspace);

        var goal = Assert.Single(kernel.Goals);
        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains($"Assigned goal {goal.Id.Value[..8]} to epic {epic.Id[..8]}", output);
        Assert.Single(new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals);
    }

    [Fact]
    public void BacklogAdd_EpicPrefix_RecordsMembership()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Creation Board").GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(
            ["backlog-add", "Creation member", "--epic", epic.Id[..8], "--no-similar"],
            new AgentOrchestratorKernel(), workspace);

        var item = Assert.Single(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
        Assert.Equal(epic.Id, portfolio.GetBacklogMembershipAsync(item.Id).GetAwaiter().GetResult()!.EpicId);
        Assert.Contains($"Assigned backlog item {item.Id[..8]} to epic {epic.Id[..8]}", output);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("backlog-add")]
    public void Creation_UnknownEpic_RejectsBeforeCreating(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var existing = backlog.AddAsync("Existing item").GetAwaiter().GetResult();
        string[] parts = command == "goal"
            ? [command, "--text-file", WriteObjective(workspace), "--epic", "missing-board"]
            : [command, "New item", "--epic", "missing-board", "--no-similar"];

        var error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(parts, kernel, workspace));

        Assert.Contains("--epic 'missing-board'", error.Message);
        Assert.Contains("No epic matched", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Equal(existing.Id, Assert.Single(backlog.ListAsync().GetAwaiter().GetResult()).Id);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("backlog-add")]
    public void Creation_AmbiguousEpic_RejectsBeforeCreating(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        portfolio.AddEpicAsync("Duplicate Board").GetAwaiter().GetResult();
        portfolio.AddEpicAsync("Duplicate Board").GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();

        var error = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            [command, "Create member", "--epic", "Duplicate Board"], kernel, workspace));

        Assert.Contains("--epic 'Duplicate Board'", error.Message);
        Assert.Contains("ambiguous", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Empty(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("backlog-add")]
    public void Creation_MissingEpicValue_RejectsBeforeCreating(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();

        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            [command, "Create member", "--epic"], kernel, workspace));

        Assert.Equal("--epic requires an epic id or title.", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.Empty(new BacklogStore(workspace.BacklogStorePath).ListAsync().GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Goal_LinkedBacklog_InheritsUnlessExplicitlyOverridden(bool overrideEpic)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var inherited = portfolio.AddEpicAsync("Inherited Board").GetAwaiter().GetResult();
        var requested = portfolio.AddEpicAsync("Requested Board").GetAwaiter().GetResult();
        var item = new BacklogStore(workspace.BacklogStorePath).AddAsync("Source item").GetAwaiter().GetResult();
        portfolio.AssignBacklogItemToEpicAsync(item.Id, inherited.Id).GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();
        var parts = new List<string>
        {
            "goal", "--text-file", WriteObjective(workspace),
            "--backlog-item", item.Id[..8], "--backlog-coverage", "full"
        };
        if (overrideEpic)
            parts.AddRange(["--epic", requested.Title]);

        var output = CreateCommittedGoal(parts, kernel, workspace);

        var goal = Assert.Single(kernel.Goals);
        Assert.Equal(item.Id, goal.SourceBacklogItemId);
        Assert.Equal(overrideEpic ? requested.Id : inherited.Id,
            portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        var overrides = Lines(output).Where(line => line.StartsWith("Explicit epic ", StringComparison.Ordinal));
        if (overrideEpic)
        {
            var line = Assert.Single(overrides);
            Assert.Contains(requested.Id[..8], line);
            Assert.Contains(inherited.Id[..8], line);
        }
        else
            Assert.Empty(overrides);
    }

    [Theory]
    [InlineData("--simple")]
    [InlineData("--run")]
    [InlineData("--from-backlog")]
    public void Goal_AliasWithEpic_RejectsAndNamesSupportedForms(string alias)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();

        var error = Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["goal", "Create member", alias, "--epic", "x"], kernel, workspace));

        Assert.Contains("goal <objective>", error.Message);
        Assert.Contains("goal --text-file <path>", error.Message);
        Assert.Contains("goal --brief-file <path>", error.Message);
        Assert.Empty(kernel.Goals);
        Assert.False(File.Exists(workspace.PortfolioStorePath));
    }

    [Theory]
    [InlineData("simple-goal")]
    [InlineData("backlog-intake")]
    [InlineData("goal")]
    public void Creation_BacklogPromotion_InheritsEpic(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Source Board").GetAwaiter().GetResult();
        var item = new BacklogStore(workspace.BacklogStorePath)
            .AddAsync("Update CLI help label", "Update the label in src/Cli/Help.cs.").GetAwaiter().GetResult();
        portfolio.AssignBacklogItemToEpicAsync(item.Id, epic.Id).GetAwaiter().GetResult();
        string[] parts = command switch
        {
            "simple-goal" => [command, "Update CLI help label", "--backlog-item", item.Id, "--backlog-coverage", "full"],
            "goal" => [command, item.Title, "--from-backlog", "--create-goal", "--backlog-coverage", "full"],
            _ => [command, item.Title, "--create-goal", "--backlog-coverage", "full"]
        };

        // Use the real persistence route: intake commits via PersistCheckpoint; simple-goal via its finalizer.
        ExecutePersistent(workspace, parts);

        var goal = Assert.Single(new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals);
        Assert.Equal(item.Id, goal.SourceBacklogItemId);
        Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
    }

    [Fact]
    public void Creation_BatchPromotion_InheritsEachEpic()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var firstEpic = portfolio.AddEpicAsync("First Board").GetAwaiter().GetResult();
        var secondEpic = portfolio.AddEpicAsync("Second Board").GetAwaiter().GetResult();
        var first = backlog.AddAsync("Update first CLI label", "Update src/First.cs.").GetAwaiter().GetResult();
        var second = backlog.AddAsync("Update second CLI label", "Update src/Second.cs.").GetAwaiter().GetResult();
        portfolio.AssignBacklogItemToEpicAsync(first.Id, firstEpic.Id).GetAwaiter().GetResult();
        portfolio.AssignBacklogItemToEpicAsync(second.Id, secondEpic.Id).GetAwaiter().GetResult();

        ExecutePersistent(workspace,
            ["backlog-intake", first.Title, second.Title, "--create-goal", "--backlog-coverage", "full"]);

        var goals = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals;
        Assert.Equal(2, goals.Count);
        foreach (var (item, epic) in new[] { (first, firstEpic), (second, secondEpic) })
        {
            var goal = Assert.Single(goals, goal => goal.SourceBacklogItemId == item.Id);
            Assert.Equal(epic.Id, portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult()!.EpicId);
        }
    }

    [Fact]
    public void Goal_PortfolioWriteFails_ExistsAndExitsZeroWithRecovery()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Failing Board").GetAwaiter().GetResult();
        using (var connection = new SqliteConnection($"Data Source={workspace.PortfolioStorePath};Pooling=False"))
        {
            connection.Open();
            using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER reject_creation_membership BEFORE INSERT ON goal_epic_memberships
                BEGIN SELECT RAISE(ABORT, 'test portfolio write rejected'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        var result = RunAppCommand(root, "goal", "--text-file", WriteObjective(workspace), "--epic", epic.Title);

        Assert.True(result.ExitCode == 0, $"exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
        var goal = Assert.Single(new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals);
        Assert.Null(portfolio.GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult());
        var line = Assert.Single(Lines(result.Stdout).Where(line => line.StartsWith("EPIC_ASSIGN_FAILED ", StringComparison.Ordinal)));
        Assert.StartsWith($"EPIC_ASSIGN_FAILED goal {goal.Id.Value} epic={epic.Id} reason=", line);
        Assert.Contains("test portfolio write rejected", line);
        Assert.Contains($"recover: epic-assign {goal.Id.Value} {epic.Id}", line);
        Assert.DoesNotContain("Assigned goal", result.Stdout);
    }

    [Fact]
    public void Goal_NoEpicOrLinkedMembership_DoesNotCreatePortfolioStore()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var item = new BacklogStore(workspace.BacklogStorePath).AddAsync("Source without epic").GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();

        var output = CreateCommittedGoal(
            ["goal", "Implement a change", "--backlog-item", item.Id, "--backlog-coverage", "full"], kernel, workspace);

        Assert.Single(kernel.Goals);
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        Assert.DoesNotContain("Assigned goal", output);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("backlog-add")]
    public void Help_CreationCommand_AdvertisesEpic(string command)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var output = ExecuteCliAndCapture([command, "--help"], new AgentOrchestratorKernel(), workspace);
        Assert.Contains("[--epic <id-or-title>]", output);
    }

    private static string WriteObjective(OrchestratorWorkspace workspace)
    {
        var file = Path.Combine(workspace.ExecutionDirectory, "creation-brief.md");
        File.WriteAllText(file, "Update the CLI help label in src/Cli/Help.cs.");
        return file;
    }

    private static string[] Lines(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string CreateCommittedGoal(IReadOnlyList<string> parts,
        AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? current = null;
        var finalized = false;
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(parts, kernel, workspace,
            ref agents, providers, ref profiles, ref current,
            finalizeGoalCreation: goal =>
            {
                if (File.Exists(workspace.PortfolioStorePath))
                    Assert.Null(new PortfolioStore(workspace.PortfolioStorePath)
                        .GetGoalMembershipAsync(goal.Id.Value).GetAwaiter().GetResult());
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
                finalized = true;
            }));
        Assert.True(finalized, "Goal creation finalizer was not invoked.");
        return output;
    }

    private static void ExecutePersistent(OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? current = null;
        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(parts,
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath), workspace,
            ref agents, providers, ref profiles, ref current));
    }
}
