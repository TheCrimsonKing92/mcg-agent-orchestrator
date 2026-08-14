using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalScopeCollisionPreflightTests : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Goal_scope_collision_advisory_keeps_goal_creation_nonblocking_without_dispatch")]
    public async Task GoalCreationRemainsNonblockingWithoutDispatch()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("scope-collision-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var kernel = new AgentOrchestratorKernel();
        _ = kernel.CreateGoal($"Existing work\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", $"Change shared feature\n\n{BacklogIntakePlanner.TargetScopeHeadingLine}\n- src/Feature/File.cs"],
            repository,
            workspace,
            ref agents,
            new InMemoryModelProviderRegistry([new ScopeCollisionRefinerProvider()]),
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("overlap-detected", output, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Xunit.Assert.Equal(2, restored.Goals.Count);
        var createdGoal = restored.GetGoal(Xunit.Assert.IsType<Goal>(currentGoal).Id);
        Xunit.Assert.NotEmpty(createdGoal.Tasks);
        Xunit.Assert.All(createdGoal.Tasks, task =>
        {
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Xunit.Assert.Null(task.LastDispatch);
            Xunit.Assert.Null(task.LastProcess);
        });
    }

    private sealed class ScopeCollisionRefinerProvider : IModelProvider
    {
        public string ProviderName => "scope-collision-refiner";

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
