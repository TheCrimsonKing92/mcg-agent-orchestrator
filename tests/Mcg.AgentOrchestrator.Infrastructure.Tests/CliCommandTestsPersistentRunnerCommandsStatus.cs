using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsPersistentRunnerCommandsStatus : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_status_loads_terminal_goal_on_demand")]
    public void PersistentRunnerStatusLoadsTerminalGoalOnDemand()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        IReadOnlyList<Func<string, string[]>> commands =
        [
            prefix => ["status", prefix],
            prefix => ["status", prefix, "--tasks-only"],
            prefix => ["status", "--tasks-only", prefix]
        ];

        foreach (var command in commands)
        {
            var kernel = new AgentOrchestratorKernel();
            var completed = kernel.CreateGoal("Completed report goal", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active report bystander");
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                command(completed.Id.Value[..8]),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Contains(completed.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(completed.Id, currentGoal!.Id);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_status_tasks_only_without_prefix_uses_current_goal")]
    public void PersistentRunnerStatusTasksOnlyWithoutPrefixUsesCurrentGoal()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("CURRENT_OBJECTIVE_TOKEN", [new TaskSpec(TaskId.New(), "Current task line", AgentRole.Developer)]);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["status", "--tasks-only"],
            repository,
            CreateRefinedWorkspace(root),
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains($"Goal {goal.Id.Value}", output);
        Xunit.Assert.Contains("Current task line", output);
        Xunit.Assert.DoesNotContain("CURRENT_OBJECTIVE_TOKEN", output);
        Xunit.Assert.Equal(goal.Id, currentGoal!.Id);
    }
}
