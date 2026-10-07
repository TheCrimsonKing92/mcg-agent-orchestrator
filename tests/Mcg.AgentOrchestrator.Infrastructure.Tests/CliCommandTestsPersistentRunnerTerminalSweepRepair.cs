using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerTerminalSweepRepair : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_goal_recovery_persists_terminal_sweep_repairs")]
    public async Task PersistentRunnerGoalRecoveryPersistsTerminalSweepRepairs()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Premature completion", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        currentGoal = kernel.GetGoal(goal.Id);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["goal-recovery", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Completed, restored.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Cancelled, restored.Tasks.Single(item => item.Id == task.Id).Status);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
    }
}
