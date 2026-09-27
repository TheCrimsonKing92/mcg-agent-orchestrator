using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsAdjudicatePrecondition : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task Cli_captures_semantic_precondition_and_keeps_version_for_audit()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Queue adjudication", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
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

        CliPersistentStateRunner.ExecuteCommand(
            ["adjudicate", "1", "close", "--text-file", textFile, "--evidence", "receipt-1"],
            repository, workspace, ref agents, providers, ref profiles, ref currentGoal);

        var intent = Assert.Single(await SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(
            workspace.SqliteStatePath, goal.Id.Value), payload.ExpectedGoalStateVersion);
        Assert.Equal(nameof(WorkTaskStatus.Failed), payload.Precondition?.TaskStatus);
        Assert.Equal(nameof(GoalStatus.Active), payload.Precondition?.GoalStatus);
        Assert.Null(payload.Precondition?.GoalCandidateCommit);
    }
}
