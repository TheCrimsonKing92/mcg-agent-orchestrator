using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerGoalScopedCreate : CliCommandTestBase
{
    [Fact]
    public async Task GoalCreate_UnrelatedGoalAndRequest_PreservesStoredBytes()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var unrelated = kernel.CreateGoal("Unrelated operator decision");
        var request = kernel.RequestHumanInput(unrelated.Id, null, "Which option?");
        await repository.SaveAsync(kernel);
        using (var conn = OpenState(workspace))
        {
            var stored = ReadUnrelatedRows(conn, unrelated.Id, request.Id.Value);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE goals SET snapshot_json = $goalJson WHERE id = $goal;
                UPDATE human_input_requests SET snapshot_json = $requestJson WHERE id = $request;
                """;
            cmd.Parameters.AddWithValue("$goal", unrelated.Id.Value);
            cmd.Parameters.AddWithValue("$request", request.Id.Value);
            cmd.Parameters.AddWithValue("$goalJson", Indent(stored.GoalJson));
            cmd.Parameters.AddWithValue("$requestJson", Indent(stored.RequestJson));
            Assert.Equal(2, cmd.ExecuteNonQuery());
        }
        UnrelatedRows before;
        using (var conn = OpenState(workspace))
            before = ReadUnrelatedRows(conn, unrelated.Id, request.Id.Value);
        GoalId? created = null;

        _ = CaptureConsole(() => created = ExecuteGoal(repository, workspace, ["goal", "Create independent goal"]));

        Assert.NotNull(created);
        var restored = await repository.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        Assert.Contains(restored.Goals, goal => goal.Id == created);
        using var afterConnection = OpenState(workspace);
        var after = ReadUnrelatedRows(afterConnection, unrelated.Id, request.Id.Value);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.GoalJson, after.GoalJson);
        Assert.Equal(before.RequestJson, after.RequestJson);
    }

    [Fact]
    public async Task ConcurrentCreates_FullCoverage_LoadsWinnerAndRejectsLoser()
    {
        var workspace = CreateWorkspace();
        var firstRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var secondRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var unrelated = kernel.CreateGoal("Unrelated goal remains outside the creation scope");
        await firstRepository.SaveAsync(kernel);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Concurrent full coverage source");
        using var commitsReady = new CountdownEvent(2);
        using var releaseCommits = new ManualResetEventSlim(false);
        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            commitsReady.Signal();
            if (!releaseCommits.Wait(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Goal creation commit release event did not arrive.");
        };
        Exception? RunCreate(SqliteOrchestratorStateRepository repository, string objective)
        {
            try
            {
                ExecuteGoal(repository, workspace,
                    ["goal", objective, "--backlog-item", item.Id, "--backlog-coverage", "full"]);
                return null;
            }
            catch (Exception ex) { return ex; }
        }
        Task<Exception?>? firstCreate = null;
        Task<Exception?>? secondCreate = null;
        Exception?[] outcomes;
        try
        {
            firstCreate = Task.Run(() => RunCreate(firstRepository, "First concurrent candidate"));
            secondCreate = Task.Run(() => RunCreate(secondRepository, "Second concurrent candidate"));
            if (!commitsReady.Wait(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Both goal creation pre-commit events did not arrive.");
            releaseCommits.Set();
            outcomes = await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            releaseCommits.Set();
            GoalCreationSideEffectDelivery.BeforeStateCommit = null;
            if (firstCreate is not null && secondCreate is not null)
                await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromMinutes(2));
        }

        Assert.Single(outcomes, outcome => outcome is null);
        var rejected = Assert.Single(outcomes, outcome => outcome is not null);
        var restored = await firstRepository.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        Assert.Contains(restored.Goals, goal => goal.Id == unrelated.Id);
        var winner = Assert.Single(restored.Goals, goal => goal.SourceBacklogItemId == item.Id);
        Assert.Contains("reason=source-backlog-active-owner-full-coverage", rejected!.Message);
        Assert.Contains($"competingGoal={winner.Id.Value}", rejected.Message);
        Assert.Contains("ownerStatus=Active", rejected.Message);
        using var conn = OpenState(workspace);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM goals";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT backlog_item_id, owner_goal_id, version FROM source_backlog_claims";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(item.Id, reader.GetString(0));
        Assert.Equal(winner.Id.Value, reader.GetString(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task GoalCreate_AmbiguousLegacyLinks_RejectsWithBothGoalIds()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Legacy ambiguous source");
        var legacy = new AgentOrchestratorKernel();
        var first = legacy.CreateGoal("First legacy owner");
        var second = legacy.CreateGoal("Second legacy owner");
        legacy.SetGoalSourceBacklogItemLink(first.Id, item.Id, SourceBacklogCoverage.Full);
        legacy.SetGoalSourceBacklogItemLink(second.Id, item.Id, SourceBacklogCoverage.Full);
        var hookRan = false;
        GoalCreationSideEffectDelivery.BeforeStateCommit = _ =>
        {
            repository.SaveAsync(legacy).GetAwaiter().GetResult();
            hookRan = true;
        };
        InvalidOperationException error;
        try
        {
            error = Assert.Throws<InvalidOperationException>(() => CaptureConsole(() => ExecuteGoal(
                repository, workspace,
                ["goal", "Reject ambiguous legacy owners", "--backlog-item", item.Id, "--backlog-coverage", "full"])));
        }
        finally { GoalCreationSideEffectDelivery.BeforeStateCommit = null; }

        Assert.True(hookRan);
        Assert.Contains("reason=legacy-owner-ambiguous", error.Message);
        var expectedIds = new[] { first.Id.Value, second.Id.Value }.OrderBy(id => id, StringComparer.Ordinal);
        Assert.Contains($"linkedGoals={string.Join(',', expectedIds)}", error.Message);
        Assert.Equal(2, (await repository.LoadAsync()).Goals.Count);
        using var conn = OpenState(workspace);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM source_backlog_claims";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task GoalCreate_BacklogDependency_ResolvesClaimOwner()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var prerequisite = await backlog.AddAsync("Prerequisite backlog item");
        var source = await backlog.AddAsync("Dependent backlog item");
        await backlog.AddDependencyAsync(source.Id,
            new BacklogDependencyTarget(prerequisite.Id, BacklogDependencyTargetKind.Backlog));
        GoalId? prerequisiteGoal = null;
        GoalId? dependentGoal = null;
        _ = CaptureConsole(() => prerequisiteGoal = ExecuteGoal(repository, workspace,
            ["goal", "Implement prerequisite", "--backlog-item", prerequisite.Id, "--backlog-coverage", "full"]));

        _ = CaptureConsole(() => dependentGoal = ExecuteGoal(repository, workspace,
            ["goal", "Implement dependent", "--backlog-item", source.Id, "--backlog-coverage", "full"]));

        Assert.NotNull(prerequisiteGoal);
        Assert.NotNull(dependentGoal);
        var restored = await repository.LoadAsync();
        var dependent = Assert.Single(restored.Goals, goal => goal.Id == dependentGoal);
        Assert.Equal(prerequisiteGoal, Assert.Single(dependent.DependsOn));
    }

    private static OrchestratorWorkspace CreateWorkspace()
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
        OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        _ = CliPersistentStateRunner.ExecuteCommand(parts, repository, workspace,
            ref agents, providers, ref profiles, ref currentGoal);
        Assert.NotNull(currentGoal);
        return currentGoal.Id;
    }

    private static SqliteConnection OpenState(OrchestratorWorkspace workspace)
    {
        var conn = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False");
        conn.Open();
        return conn;
    }

    private static string Indent(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static UnrelatedRows ReadUnrelatedRows(SqliteConnection conn, GoalId goalId, string requestId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.version, g.updated_at, g.snapshot_json, r.snapshot_json
            FROM goals g JOIN human_input_requests r ON r.goal_id = g.id
            WHERE g.id = $goal AND r.id = $request
            """;
        cmd.Parameters.AddWithValue("$goal", goalId.Value);
        cmd.Parameters.AddWithValue("$request", requestId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new UnrelatedRows(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private sealed record UnrelatedRows(long Version, string UpdatedAt, string GoalJson, string RequestJson);

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
