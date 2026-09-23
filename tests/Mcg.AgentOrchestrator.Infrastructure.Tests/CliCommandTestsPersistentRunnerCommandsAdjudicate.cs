using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsAdjudicate : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task Adjudicate_cli_queues_typed_versioned_agent_intent_without_mutating_goal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Queue atomic adjudication", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
        await repository.SaveAsync(kernel);
        var textFile = Path.Combine(root, "adjudication.txt");
        File.WriteAllText(textFile, "Close using operator evidence.");
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var changed = CliPersistentStateRunner.ExecuteCommand(
            ["adjudicate", "1", "close", "--text-file", textFile, "--evidence", "receipt-1", "--actor-kind", "agent"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);

        var intent = Assert.Single(await SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(
            intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var currentVersion = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
            workspace.SqliteStatePath,
            goal.Id.Value);
        Assert.False(changed);
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal(currentVersion, payload.ExpectedGoalStateVersion);
        Assert.Equal(["receipt-1"], payload.EvidenceReferences);
        Assert.Equal("Close using operator evidence.", payload.Text);
        Assert.Equal(WorkTaskStatus.Failed, (await repository.LoadGoalAsync(goal.Id))!.Tasks.Single().Status);
    }
}
