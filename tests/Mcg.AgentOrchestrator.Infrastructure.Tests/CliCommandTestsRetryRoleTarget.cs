using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns a unique workspace and intent database; collection matches the existing CLI environment fixtures.
[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsRetryRoleTarget : CliCommandTestBase
{
    [Fact]
    public async Task GoalScopedRoleRetryRequeuesDeveloperAndPreservesPlanner()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var roles = new[] { AgentRole.Planner, AgentRole.Researcher, AgentRole.Developer,
                AgentRole.Tester, AgentRole.Reviewer };
            var goal = kernel.CreateGoal("Retry developer by role", roles.Select(role =>
                new TaskSpec(TaskId.New(), $"Do {role} work", role)).ToArray());
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            kernel.ActivateGoal(goal.Id, agents);
            var planner = goal.Tasks[0];
            var developer = goal.Tasks[2];
            kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Planner done");
            kernel.ReportTaskProgress(goal.Id, goal.Tasks[1].Id, WorkTaskStatus.Completed, "Research done");
            kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Failed, "Needs repair");
            var plannerStatus = planner.Status;
            var plannerRetryAt = planner.LatestRetryAt;
            var plannerRetryCount = planner.CriterionRetryCount;
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var channel = new DiscordOperatorChannel(
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));

            CaptureConsole(() =>
            {
                Assert.False(CliPersistentStateRunner.ExecuteCommand(
                    ["retry", "--goal", goal.Id.Value[..8], "developer", "--cause",
                        "NewSourceFinding", "Rework developer"],
                    repository, workspace, ref agents, providers, ref profiles, ref currentGoal, channel));
            });

            var store = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory, workspace.LogDirectory);
            var intent = Assert.Single(await store.ListForGoalAsync(
                goal.Id.Value, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(OperatorIntentVerbs.Retry, intent.Verb);
            Assert.Equal(developer.Id.Value, intent.TaskId);
            Assert.Equal(OperatorIntentStatus.Pending, intent.Status);

            // Drain the persisted state, as the conductor does, rather than the original in-memory goal.
            var persisted = (await repository.LoadGoalAsync(
                goal.Id, TestContext.Current.CancellationToken))!;
            var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([persisted], []));
            var restoredGoal = restored.GetGoal(goal.Id);
            Assert.Equal(WorkTaskStatus.Failed, restoredGoal.Tasks[2].Status);
            var result = new OperatorIntentCoordinator(store).ExecutePending(restored, restoredGoal);

            Assert.True(result.MutatedGoalState);
            var retried = Assert.Single(restoredGoal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
            Assert.Equal(developer.Id, retried.TaskId);
            Assert.Equal(WorkTaskStatus.Assigned, restoredGoal.Tasks[2].Status);
            Assert.NotNull(restoredGoal.Tasks[2].LatestRetryAt);
            Assert.Equal("Rework developer", restoredGoal.Tasks[2].AcceptedRetryFeedback?.Message);
            Assert.Equal(plannerStatus, restoredGoal.Tasks[0].Status);
            Assert.Equal(plannerRetryAt, restoredGoal.Tasks[0].LatestRetryAt);
            Assert.Equal(plannerRetryCount, restoredGoal.Tasks[0].CriterionRetryCount);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
