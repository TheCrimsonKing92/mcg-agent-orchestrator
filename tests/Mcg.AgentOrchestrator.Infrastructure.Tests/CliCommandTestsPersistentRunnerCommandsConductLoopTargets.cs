using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsConductLoopTargets : CliCommandTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void IntentPreparation_RefreshesOnlyTargetAndDefersInconclusiveGit(bool notRepository)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Target");
            kernel.ReportTaskProgress(target.Id, target.Tasks.Single().Id, WorkTaskStatus.Failed, "repair");
            var other = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Unrelated");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var stale = kernel.ExportGoalSnapshot(other.Id);
            kernel.ReplaceGoalWithSnapshot(stale with
            {
                Tasks = stale.Tasks.Select(task => task with { Status = WorkTaskStatus.WaitingForHuman }).ToArray()
            });
            var unrelated = kernel.GetGoal(other.Id);
            var context = new CliExecutionContext(kernel, CreateRefinedWorkspace(root), new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), currentGoal: null,
                loadOperatorIntentTargets: ids => repository.LoadGoalsAsync(ids).GetAwaiter().GetResult());
            var observations = new Dictionary<string, ConductorGoalReloadObservation>();
            var probes = 0;

            var ready = OperatorIntentTargetPreparation.PrepareOperatorIntentTarget(context, kernel, target.Id,
                new BackgroundDispatchRunner(isStillRunning: _ => false), observations,
                (_, args) =>
                {
                    probes++;
                    Assert.Equal(new[] { "rev-parse", "--is-inside-work-tree" }, args);
                    return new GitCli.GitResult(128, string.Empty,
                        notRepository ? "fatal: not a git repository (or any of the parent directories): .git" : "fatal: unable to read repository");
                });

            Assert.Equal(notRepository, ready);
            Assert.Equal(1, probes);
            Assert.Equal(new[] { target.Id.Value }, Assert.Single(repository.LoadGoalBatches));
            Assert.Same(unrelated, kernel.GetGoal(other.Id));
            Assert.Equal(WorkTaskStatus.WaitingForHuman, unrelated.Tasks.Single().Status);
            Assert.Equal(WorkTaskStatus.Failed, kernel.GetGoal(target.Id).Tasks.Single().Status);
            Assert.IsType<ConductorGoalReloadObservation.NotObserved>(observations[target.Id.Value]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void IntentTargetLoad_HydratesOnlyRequestedGoalAndRefreshesItsBaseline()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Target");
        var other = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Other");
        var targetBefore = kernel.ExportGoalSnapshot(target.Id);
        var otherBefore = kernel.ExportGoalSnapshot(other.Id);
        kernel.ReportTaskProgress(target.Id, target.Tasks.Single().Id, WorkTaskStatus.Failed, "fresh stored state");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var baselines = new Dictionary<string, GoalSnapshot>
        {
            [target.Id.Value] = targetBefore,
            [other.Id.Value] = otherBefore
        };

        var loaded = ConductLoopTargetLoader.LoadOperatorIntentTargets(repository, [target.Id], baselines);

        Assert.Equal(target.Id, Assert.Single(loaded.Goals).Id);
        Assert.Equal(new[] { target.Id.Value }, Assert.Single(repository.LoadGoalBatches));
        Assert.Equal(0, repository.LoadCount);
        Assert.Equal(0, repository.LoadGoalCount);
        Assert.Equal(WorkTaskStatus.Failed, baselines[target.Id.Value].Tasks.Single().Status);
        Assert.Same(otherBefore, baselines[other.Id.Value]);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_targeted_query_ignores_synthetic_parked_wait_completion")]
    public void PersistentRunnerConductLoopTargetedQueryIgnoresSyntheticParkedWaitCompletion()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked synthetic wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RequestHumanInput(target.Id, targetTask.Id, "Need operator decision.");
        kernel.ParkGoal(target.Id, "waiting for operator answer");

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var targetedKernel = CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository);
        var promoted = targetedKernel.RefreshParkedGoalsWithResolvedHumanWaits();

        Xunit.Assert.Equal(1, repository.CompletedHumanInputQueryCount);
        Xunit.Assert.DoesNotContain(target.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Equal(0, promoted);
        Xunit.Assert.Empty(targetedKernel.Goals);
    }
}
