using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsScoutGoalCreation : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("five-role")]
    [Xunit.InlineData("scout")]
    public async Task GoalCreateForcedPipelinePersistsExactTaskOrder(string pipeline)
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
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([new ClarifyingGoalRefinerProvider()]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        const string objective = "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with one focused assertion.";

        var automaticPlan = GoalObjectivePlanner.Build(objective);
        Xunit.Assert.Equal(GoalIntakePipeline.Scout, automaticPlan.PipelineDecision.Pipeline);
        Xunit.Assert.False(automaticPlan.PipelineDecision.IsOverride);

        _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["goal", objective, "--pipeline", pipeline],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var persistedGoal = Xunit.Assert.Single(restored.Goals);
        Xunit.Assert.Equal(
            pipeline == "scout"
                ? (AgentRole[])[AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer]
                : (AgentRole[])[AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            persistedGoal.Tasks.Select(task => task.RequiredRole));
    }
}
