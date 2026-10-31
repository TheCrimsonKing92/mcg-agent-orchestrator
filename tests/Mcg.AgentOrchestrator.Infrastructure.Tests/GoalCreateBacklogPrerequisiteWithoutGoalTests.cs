using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class GoalCreateBacklogPrerequisiteWithoutGoalTests : CliCommandTestBase
{
    [Fact]
    public async Task DonePrerequisiteWithoutOwner_CreatesLinkedGoalWithoutDependency()
    {
        var (workspace, repository, backlog, prerequisite, dependent) = await CreateDependencyAsync();
        await backlog.CloseAsync(prerequisite.Id);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        Assert.Null(claimStore.ResolveClaim(await repository.LoadAsync(), prerequisite.Id));

        _ = CaptureConsole(() => ExecuteGoal(repository, workspace, dependent.Id));

        var restored = await repository.LoadAsync();
        var goal = Assert.Single(restored.Goals);
        Assert.Equal(dependent.Id, goal.SourceBacklogItemId);
        Assert.Empty(goal.DependsOn);
        var claim = claimStore.ResolveClaim(restored, dependent.Id);
        Assert.NotNull(claim);
        Assert.Equal(goal.Id.Value, claim.OwnerGoalId);
        var storedPrerequisite = await backlog.GetByExactIdAsync(prerequisite.Id);
        Assert.NotNull(storedPrerequisite);
        Assert.Equal(BacklogItemStatus.Done, storedPrerequisite.Status);
        Assert.Null(claimStore.ResolveClaim(restored, prerequisite.Id));
        var storedDependent = await backlog.GetByExactIdAsync(dependent.Id);
        Assert.NotNull(storedDependent);
        var dependency = Assert.Single(storedDependent.Dependencies);
        Assert.Equal(prerequisite.Id, dependency.PrerequisiteId);
        Assert.Equal(BacklogDependencyTargetKind.Backlog, dependency.TargetKind);
    }

    [Theory]
    [InlineData(BacklogItemStatus.Open)]
    [InlineData(BacklogItemStatus.Superseded)]
    public async Task PrerequisiteChangedBeforeCommit_RejectsWithExactStatusReason(
        BacklogItemStatus status)
    {
        var (workspace, repository, backlog, prerequisite, dependent) = await CreateDependencyAsync();
        await backlog.CloseAsync(prerequisite.Id);
        var replacement = await backlog.AddAsync("Unrelated replacement");
        var hookCalls = 0;
        InvalidOperationException error;
        try
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            {
                hookCalls++;
                Assert.Equal(BacklogItemStatus.Done,
                    backlog.GetByExactIdAsync(prerequisite.Id).GetAwaiter().GetResult()!.Status);
                var changed = status == BacklogItemStatus.Open
                    ? backlog.ReopenAsync(prerequisite.Id).GetAwaiter().GetResult()
                    : backlog.SupersedeAsync(prerequisite.Id, replacement.Id).GetAwaiter().GetResult();
                Assert.Equal(status, changed.Status);
            };
            error = Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                ExecuteGoal(repository, workspace, dependent.Id)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Assert.Equal(1, hookCalls);
        Assert.Equal(
            $"GOAL_CREATE_PRECONDITION_CHANGED reason=dependency-prerequisite-not-done backlogItem={dependent.Id} dependency={prerequisite.Id} prerequisiteStatus={status}",
            error.Message);
        Assert.Equal("dependency-prerequisite-not-done", GoalCreationPreconditionValidator.GetReason(error));
        var restored = await repository.LoadAsync();
        Assert.Empty(restored.Goals);
        Assert.Null(new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, dependent.Id));
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Fact]
    public async Task PrerequisiteMissingBeforeCommit_RejectsWithExactMissingReason()
    {
        var (workspace, repository, backlog, prerequisite, dependent) = await CreateDependencyAsync();
        await backlog.CloseAsync(prerequisite.Id);
        var unknownId = Guid.NewGuid().ToString("n");
        var hookCalls = 0;
        InvalidOperationException error;
        try
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
            {
                hookCalls++;
                using var connection = new SqliteConnection($"Data Source={workspace.BacklogStorePath};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE backlog_dependencies SET prerequisite_id = $missing WHERE dependent_id = $source";
                command.Parameters.AddWithValue("$missing", unknownId);
                command.Parameters.AddWithValue("$source", dependent.Id);
                Assert.Equal(1, command.ExecuteNonQuery());
            };
            error = Assert.Throws<InvalidOperationException>(() => CaptureConsole(() =>
                ExecuteGoal(repository, workspace, dependent.Id)));
        }
        finally
        {
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
        }

        Assert.Equal(1, hookCalls);
        Assert.Equal(
            $"GOAL_CREATE_PRECONDITION_CHANGED reason=dependency-target-missing backlogItem={dependent.Id} dependency={unknownId}",
            error.Message);
        Assert.Equal("dependency-target-missing", GoalCreationPreconditionValidator.GetReason(error));
        Assert.Empty((await repository.LoadAsync()).Goals);
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
    }

    [Fact]
    public async Task OwnedOpenPrerequisite_KeepsExactlyItsGoalDependency()
    {
        await AssertOwnedPrerequisiteDependencyAsync(closePrerequisite: false);
    }

    [Fact]
    public async Task OwnedDonePrerequisite_KeepsExactlyItsGoalDependency()
    {
        await AssertOwnedPrerequisiteDependencyAsync(closePrerequisite: true);
    }

    [Fact]
    public async Task NoPrerequisites_CreatesGoalWithoutDependencies()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var dependent = await backlog.AddAsync("Dependent without prerequisites");

        _ = CaptureConsole(() => ExecuteGoal(repository, workspace, dependent.Id));

        var goal = Assert.Single((await repository.LoadAsync()).Goals);
        Assert.Equal(dependent.Id, goal.SourceBacklogItemId);
        Assert.Empty(goal.DependsOn);
    }

    private async Task AssertOwnedPrerequisiteDependencyAsync(bool closePrerequisite)
    {
        var (workspace, repository, backlog, prerequisite, dependent) = await CreateDependencyAsync();
        GoalId? prerequisiteGoalId = null;
        _ = CaptureConsole(() => prerequisiteGoalId = ExecuteGoal(repository, workspace, prerequisite.Id));
        Assert.NotNull(prerequisiteGoalId);
        if (closePrerequisite)
            await backlog.CloseAsync(prerequisite.Id);
        Assert.Equal(closePrerequisite ? BacklogItemStatus.Done : BacklogItemStatus.Open,
            (await backlog.GetByExactIdAsync(prerequisite.Id))!.Status);

        _ = CaptureConsole(() => ExecuteGoal(repository, workspace, dependent.Id));

        var restored = await repository.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        var goal = Assert.Single(restored.Goals, candidate => candidate.SourceBacklogItemId == dependent.Id);
        Assert.Equal(prerequisiteGoalId, Assert.Single(goal.DependsOn));
    }

    private async Task<(OrchestratorWorkspace Workspace, SqliteOrchestratorStateRepository Repository,
        BacklogStore Backlog, BacklogItem Prerequisite, BacklogItem Dependent)> CreateDependencyAsync()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var prerequisite = await backlog.AddAsync("Prerequisite backlog item");
        var dependent = await backlog.AddAsync("Dependent backlog item");
        await backlog.AddDependencyAsync(dependent.Id,
            new BacklogDependencyTarget(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        return (workspace, repository, backlog, prerequisite, dependent);
    }

    private OrchestratorWorkspace CreateWorkspace()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("clarifying-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        return workspace;
    }

    private static GoalId ExecuteGoal(SqliteOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, string backlogItemId)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        _ = CliPersistentStateRunner.ExecuteCommand(
            ["goal", "Implement dependent", "--backlog-item", backlogItemId, "--backlog-coverage", "slice"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal);
        Assert.NotNull(currentGoal);
        return currentGoal.Id;
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
                  "forks": [{
                    "kind": "external-contract",
                    "topicKey": "goal-create-api-contract",
                    "refinerConfidence": "low",
                    "blastRadius": "high",
                    "question": "Which external API contract should the goal target?",
                    "choice": "",
                    "rationale": "Operator decision required."
                  }]
                }
                ```
                """;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }
}
