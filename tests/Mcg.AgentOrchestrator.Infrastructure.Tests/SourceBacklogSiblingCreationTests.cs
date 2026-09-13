using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class SourceBacklogSiblingCreationTests : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_active_slice_owner_allows_sibling_goal")]
    public async Task PersistentRunnerActiveSliceOwnerAllowsSiblingGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Multi-slice backlog source");
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Implement first active slice", "--backlog-item", item.Id, "--backlog-coverage", "slice"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var firstGoalId = currentGoal!.Id;
        var incumbent = Xunit.Assert.Single((await repository.LoadAsync()).Goals);
        Xunit.Assert.Equal(GoalStatus.Active, incumbent.Status);

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Implement second active slice", "--backlog-item", item.Id, "--backlog-coverage", "slice"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        Xunit.Assert.All(restored.Goals, goal => Xunit.Assert.Equal(item.Id, goal.SourceBacklogItemId));
        var claim = new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, item.Id);
        Xunit.Assert.NotNull(claim);
        Xunit.Assert.Equal(firstGoalId.Value, claim.OwnerGoalId);
        Xunit.Assert.Equal(1, claim.Version);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_concurrent_goal_creates_have_exactly_one_backlog_winner")]
    public async Task PersistentRunnerConcurrentGoalCreatesHaveExactlyOneBacklogWinner()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Concurrent single-consumer source");
        var firstRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var secondRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);

        Exception? RunCreate(ITransactionalOrchestratorStateRepository repository, string objective)
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            try
            {
                _ = CliPersistentStateRunner.ExecuteCommand(
                    ["goal", objective, "--backlog-item", item.Id, "--backlog-coverage", "full"],
                    repository,
                    workspace,
                    ref localAgents,
                    providers,
                    ref localProfiles,
                    ref localGoal);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        using var commitsReady = new CountdownEvent(initialCount: 2);
        using var releaseCommits = new ManualResetEventSlim(initialState: false);
        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            commitsReady.Signal();
            Xunit.Assert.True(releaseCommits.Wait(TimeSpan.FromSeconds(15)));
        };
        Exception?[] outcomes;
        try
        {
            var firstCreate = Task.Run(() => RunCreate(firstRepository, "Concurrent intake candidate one"));
            var secondCreate = Task.Run(() => RunCreate(secondRepository, "Concurrent intake candidate two"));
            Xunit.Assert.True(commitsReady.Wait(TimeSpan.FromSeconds(15)));
            releaseCommits.Set();
            outcomes = await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            releaseCommits.Set();
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }
        Xunit.Assert.Single(outcomes, outcome => outcome is null);
        var rejected = Xunit.Assert.Single(outcomes, outcome => outcome is not null);
        Xunit.Assert.Contains(
            "GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-active-owner-full-coverage",
            rejected!.Message);

        var restored = await firstRepository.LoadAsync();
        var owner = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(item.Id, owner.SourceBacklogItemId);
        var claim = new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, item.Id);
        Xunit.Assert.NotNull(claim);
        Xunit.Assert.Equal(owner.Id.Value, claim.OwnerGoalId);
        Xunit.Assert.Null(owner.RefinedSpec);
        Xunit.Assert.Equal(GoalStatus.Active, owner.Status);
        Xunit.Assert.Equal(1, claim.Version);
        Xunit.Assert.Single(Directory.GetFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl"));
    }

    private sealed class ClarifyingGoalRefinerProvider : IModelProvider
    {
        public string ProviderName => "clarifying-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            const string response = """
                ```json
                {
                  "behavioralContract": "Integrate with an external API selected by the operator.",
                  "acceptanceCriteria": ["The selected API contract is implemented."],
                  "verificationClass": "TestVerifiable",
                  "decisions": [],
                  "forks": [
                    {
                      "kind": "external-contract",
                      "topicKey": "goal-create-api-contract",
                      "refinerConfidence": "low",
                      "blastRadius": "high",
                      "question": "Which external API contract should the goal target?",
                      "choice": "",
                      "rationale": "Operator decision required."
                    }
                  ]
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }

}
