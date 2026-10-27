using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class GoalCreateDependsOnFlagTests : CliCommandTestBase
{
    [Fact]
    public async Task Creation_PersistsEdgeInInitialWriteAndHoldsDispatch()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var prerequisite = CreatePrerequisite(kernel, "Prerequisite A");
        await repository.SaveAsync(kernel);
        var brief = WriteBrief();

        var createdId = ExecuteGoal(repository, workspace,
            ["goal", "--brief-file", brief, "--depends-on", prerequisite.Id.Value[..8]]);
        var siblingId = ExecuteGoal(repository, workspace, ["goal", "--brief-file", brief]);

        var restored = await repository.LoadAsync();
        var created = Assert.Single(restored.Goals, goal => goal.Id == createdId);
        Assert.Equal(prerequisite.Id, Assert.Single(created.DependsOn));
        Assert.Equal(ReadVersion(workspace, siblingId), ReadVersion(workspace, createdId));
        var conductKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var conductGoal = Assert.Single(conductKernel.Goals, goal => goal.Id == createdId);
        Assert.Equal($"waiting on dependency {prerequisite.Id.Value[..8]}",
            ConductorDependencyHoldEvaluator.Evaluate(conductGoal, [], [], conductKernel, out _));
        Assert.Null(ConductorDependencyHoldEvaluator.Evaluate(conductGoal,
            [prerequisite.Id.Value], [], conductKernel, out _));
    }

    [Fact]
    public async Task RepeatedArguments_DeduplicateResolvedGoals()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var first = CreatePrerequisite(kernel, "Prerequisite A");
        var second = CreatePrerequisite(kernel, "Prerequisite B");
        await repository.SaveAsync(kernel);

        var createdId = ExecuteGoal(repository, workspace,
            ["goal", "--brief-file", WriteBrief(), "--depends-on", first.Id.Value[..8],
                "--depends-on", second.Id.Value[..8], "--depends-on", first.Id.Value[..8],
                "--depends-on", first.Id.Value]);

        var created = Assert.Single((await repository.LoadAsync()).Goals, goal => goal.Id == createdId);
        Assert.Equal(2, created.DependsOn.Count);
        Assert.Contains(first.Id, created.DependsOn);
        Assert.Contains(second.Id, created.DependsOn);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    [InlineData("cancelled")]
    [InlineData("missing")]
    [InlineData("flag-value")]
    [InlineData("empty")]
    [InlineData("simple")]
    [InlineData("run")]
    [InlineData("from-backlog")]
    [InlineData("backlog-item")]
    public async Task Refusal_CreatesNoStoredGoal(string scenario)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var prerequisite = CreatePrerequisite(kernel, "Prerequisite A");
        if (scenario == "ambiguous")
        {
            kernel.CreateGoal(new GoalId("abcdef01000000000000000000000001"), "Ambiguous A");
            kernel.CreateGoal(new GoalId("abcdef02000000000000000000000002"), "Ambiguous B");
        }
        if (scenario == "cancelled")
            kernel.CancelGoal(prerequisite.Id, "Fixture cancellation");
        await repository.SaveAsync(kernel);
        var beforeCount = (await repository.LoadAsync()).Goals.Count;
        var parts = new List<string> { "goal", "--brief-file", WriteBrief(), "--depends-on" };
        if (scenario != "missing")
            parts.Add(scenario switch
            {
                "unknown" => "zzzz9999",
                "ambiguous" => "abcdef0",
                "flag-value" => "--simple",
                "empty" => " ",
                _ => prerequisite.Id.Value[..8]
            });
        switch (scenario)
        {
            case "simple": parts.Add("--simple"); break;
            case "run": parts.AddRange(["--run", "--confirm-batch-start"]); break;
            case "from-backlog": parts.Add("--from-backlog"); break;
            case "backlog-item":
                var backlogItem = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Fixture backlog item");
                parts.AddRange(["--backlog-item", backlogItem.Id, "--backlog-coverage", "full"]);
                break;
        }

        var exception = Record.Exception(() => ExecuteCommand(repository, workspace, parts));

        switch (scenario)
        {
            case "unknown":
                Assert.Contains("zzzz9999", Assert.IsType<KeyNotFoundException>(exception).Message);
                break;
            case "ambiguous":
                Assert.Contains("ambiguous", Assert.IsType<InvalidOperationException>(exception).Message);
                break;
            case "cancelled":
                var rejected = Assert.IsType<InvalidOperationException>(exception);
                Assert.Contains("GOAL_DEPENDS_ON_REJECTED", rejected.Message);
                Assert.Contains(prerequisite.Id.Value[..8], rejected.Message);
                Assert.Contains("Cancelled", rejected.Message);
                break;
            default:
                var invalid = Assert.IsType<ArgumentException>(exception);
                if (scenario is "missing" or "flag-value" or "empty")
                    Assert.Equal("--depends-on requires a goal prefix.", invalid.Message);
                else
                {
                    Assert.Contains("goal <objective> --depends-on <goal-prefix>", invalid.Message);
                    Assert.Contains("goal --brief-file <path> --depends-on <goal-prefix>", invalid.Message);
                }
                break;
        }
        Assert.Equal(beforeCount, (await repository.LoadAsync()).Goals.Count);
    }

    [Fact]
    public async Task FlaglessCreation_HasNoHoldBeforeOrAfterPrerequisiteExists()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var brief = WriteBrief();
        var firstId = ExecuteGoal(repository, workspace, ["goal", "--brief-file", brief]);
        var beforeKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var first = Assert.Single(beforeKernel.Goals, goal => goal.Id == firstId);
        Assert.Empty(first.DependsOn);
        var beforeHold = ConductorDependencyHoldEvaluator.Evaluate(first, [], [], beforeKernel, out _);
        Assert.Null(beforeHold);
        var kernel = await repository.LoadAsync();
        var prerequisite = CreatePrerequisite(kernel, "Prerequisite A");
        await repository.SaveAsync(kernel);

        var secondId = ExecuteGoal(repository, workspace, ["goal", "--brief-file", brief]);

        var afterKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var second = Assert.Single(afterKernel.Goals, goal => goal.Id == secondId);
        Assert.Empty(second.DependsOn);
        var afterHold = ConductorDependencyHoldEvaluator.Evaluate(second, [], [], afterKernel, out _);
        Assert.Null(afterHold);
        Assert.Equal(beforeHold, afterHold);

        var dependentId = ExecuteGoal(repository, workspace,
            ["goal", "--brief-file", brief, "--depends-on", prerequisite.Id.Value[..8]]);
        var dependentKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        var dependent = Assert.Single(dependentKernel.Goals, goal => goal.Id == dependentId);
        Assert.Equal(prerequisite.Id, Assert.Single(dependent.DependsOn));
        Assert.Equal($"waiting on dependency {prerequisite.Id.Value[..8]}",
            ConductorDependencyHoldEvaluator.Evaluate(dependent, [], [], dependentKernel, out _));
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("text-file")]
    public async Task PlainInputForms_RecordDependency(string form)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var prerequisite = CreatePrerequisite(kernel, "Prerequisite A");
        await repository.SaveAsync(kernel);
        var parts = new List<string> { "goal" };
        if (form == "text-file")
            parts.AddRange(["--text-file", WriteBrief()]);
        else
            parts.Add("Dependent goal brief");
        parts.AddRange(["--depends-on", prerequisite.Id.Value[..8]]);

        var createdId = ExecuteGoal(repository, workspace, parts);

        var created = Assert.Single((await repository.LoadAsync()).Goals, goal => goal.Id == createdId);
        Assert.Equal(prerequisite.Id, Assert.Single(created.DependsOn));
    }

    [Fact]
    public void Fingerprint_IncludesDependenciesAndNormalizesOrderAndCase()
    {
        var workspace = CreateWorkspace();
        var agents = AgentCatalog.Default().Agents;
        string Fingerprint(params string[] dependencies) => GoalIntakeRequestMapper.Map(
            ["goal", "Objective text", "--request-key", "k1", ..dependencies], workspace, agents)!.Fingerprint;

        Assert.NotEqual(Fingerprint(), Fingerprint("--depends-on", "abc12345"));
        Assert.Equal(Fingerprint("--depends-on", "abc12345", "--depends-on", "def67890"),
            Fingerprint("--depends-on", "DEF67890", "--depends-on", "ABC12345"));
        Assert.NotEqual(Fingerprint("--depends-on", "abc12345"),
            Fingerprint("--depends-on", "abc12345", "--depends-on", "abc12345"));
    }

    [Fact]
    public void Help_AcceptsAndDescribesDependencyFlag()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["goal", "--brief-file", "brief.md", "--depends-on", "abc12345"]);
        Assert.Contains("--depends-on <goal-prefix>", CliCommandHelp.GoalUsage);
        Assert.Contains("repeatable", CliCommandHelp.GoalUsage);
    }

    private static Goal CreatePrerequisite(AgentOrchestratorKernel kernel, string objective) =>
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, objective);

    private static string WriteBrief()
    {
        var path = Path.Combine(CreateTempDirectory(), "brief.md");
        File.WriteAllText(path, "Dependent goal brief");
        return path;
    }

    private static OrchestratorWorkspace CreateWorkspace()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("fixture-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        return workspace;
    }

    private static GoalId ExecuteGoal(SqliteOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        var goal = ExecuteCommand(repository, workspace, parts);
        Assert.NotNull(goal);
        return goal.Id;
    }

    private static Goal? ExecuteCommand(SqliteOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var providers = new InMemoryModelProviderRegistry([new FixtureRefinerProvider()]);
        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(parts, repository, workspace,
            ref agents, providers, ref profiles, ref currentGoal));
        return currentGoal;
    }

    private static long ReadVersion(OrchestratorWorkspace workspace, GoalId goalId)
    {
        using var connection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM goals WHERE id = $goal";
        command.Parameters.AddWithValue("$goal", goalId.Value);
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FixtureRefinerProvider : IModelProvider
    {
        public string ProviderName => "fixture-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse("""
                {"behavioralContract":"Dependent goal brief","acceptanceCriteria":["Implement the brief."],
                 "verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                """, new ModelUsage(1, 1), "stop"));
    }
}
