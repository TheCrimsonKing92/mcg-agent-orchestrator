using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsDispatchRecovery : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_recover_reconciles_terminal_running_exit_file")]
    public async Task PersistentRunnerRecoverReconcilesTerminalRunningExitFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("No implicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        goal = kernel.GetGoal(goal.Id);
        currentGoal = goal;
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["recover", goal.Id.Value[..8], "operator note"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_next_persists_terminal_sweep_repairs")]
    public async Task PersistentRunnerNextPersistsTerminalSweepRepairs()
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
            ["next", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(GoalStatus.Active, restored.Status);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_applies_exit_file_outside_command_transaction")]
    public async Task PersistentRunnerReconcileAppliesExitFileOutsideCommandTransaction()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_uses_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchUsesGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit refresh", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.Contains("Dispatch state:", output);
        Xunit.Assert.Contains("Next action:", output);
        Xunit.Assert.DoesNotContain("Task timeline:", output);

        var restoredSnapshot = await repository.LoadGoalAsync(goal.Id);
        var restoredTask = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([restoredSnapshot!], [])).GetTask(goal.Id, task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTask.Status);
        Xunit.Assert.Equal(0, restoredTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTask.LastVerification);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_history_limit_matches_compact_command_contract")]
    public void PersistentRunnerRefreshDispatchHistoryLimitMatchesCompactCommandContract()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit bounded refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        for (var index = 0; index < 12; index++)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"PERSISTENT_HISTORY_{index:00}");
        }
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, """
            WORKER_RESULT:
            files: none
            commands: inspected dispatch output
            tests: pass - focused verification passed
            commit: none
            blockers: none
            model_fit: OpenAI/gpt-test - adequate - verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history-limit", "4"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var timeline = output[(output.IndexOf("Task timeline:", StringComparison.Ordinal) + "Task timeline:".Length)..];
        var newest = timeline.IndexOf("PERSISTENT_HISTORY_11", StringComparison.Ordinal);
        var verification = timeline.IndexOf("Dispatch execution passed", StringComparison.Ordinal);
        var completion = timeline.IndexOf("Dispatch completed successfully", StringComparison.Ordinal);

        Xunit.Assert.Equal(4, CountNonEmptyLines(timeline));
        Xunit.Assert.True(newest >= 0 && newest < verification && verification < completion, timeline);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_history_prints_complete_history_oldest_first")]
    public void PersistentRunnerRefreshDispatchHistoryPrintsCompleteHistoryOldestFirst()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Explicit full refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        for (var index = 0; index < 12; index++)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"PERSISTENT_HISTORY_{index:00}");
        }
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var first = output.IndexOf("PERSISTENT_HISTORY_00", StringComparison.Ordinal);
        var middle = output.IndexOf("PERSISTENT_HISTORY_06", StringComparison.Ordinal);
        var newest = output.IndexOf("PERSISTENT_HISTORY_11", StringComparison.Ordinal);
        var verification = output.IndexOf("Dispatch execution passed", StringComparison.Ordinal);
        var completion = output.IndexOf("Dispatch completed successfully", StringComparison.Ordinal);

        Xunit.Assert.Contains("Task timeline:", output);
        Xunit.Assert.True(
            first >= 0 && first < middle && middle < newest && newest < verification && verification < completion,
            output);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_invalid_refresh_history_limit_does_not_load_or_mutate_goal_state")]
    public void PersistentRunnerInvalidRefreshHistoryLimitDoesNotLoadOrMutateGoalState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Reject invalid refresh history", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var exception = Xunit.Assert.Throws<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1", "--history-limit", "1", "--history-limit", "2"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains(CliCommandHelp.RefreshDispatchUsage, exception.Message);
        Xunit.Assert.Equal(0, repository.LoadGoalsCount);
        Xunit.Assert.Equal(0, repository.TransactionCount);
        Xunit.Assert.True(task.LastProcess!.IsRunning);
        Xunit.Assert.Null(task.LastVerification);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatch_atomically_persists_premise_invalid_human_wait")]
    public async Task PersistentRunnerRefreshDispatchAtomicallyPersistsPremiseInvalidHumanWait()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Validate the implementation premise", AgentRole.Planner);
        var goal = kernel.CreateGoal("Premise validation canary", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, """
            WORKER_RESULT:
            files: none
            commands: inspected src/Mcg.AgentOrchestrator.Core/Domain/OrchestrationEnums.cs
            tests: not-run - read-only premise validation
            commit: none
            blockers: premise-invalid - AgentRole.Judge is absent from the inspected enum at the current HEAD
            model_fit: Anthropic/claude-opus-5 - adequate - premise validation - sufficient
            skills: orchestrator-worker-verification
            confidence: high
            END_WORKER_RESULT
            """);
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatch", goal.Id.Value[..8], "1"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restored = await repository.LoadAsync();
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, restoredTask.Status);
        Xunit.Assert.True(restoredTask.LastVerification?.WorkerResultPresent);
        Xunit.Assert.Equal(1, repository.LastSavedGoalStateHumanInputCount);
        var request = Xunit.Assert.Single(restored.HumanInputRequests);
        Xunit.Assert.False(request.IsCompleted);
        Xunit.Assert.Contains("premise-invalid", request.Question, StringComparison.Ordinal);
        Xunit.Assert.Equal(task.Id, request.TaskId);
        Xunit.Assert.Equal(0, repository.TransactAsyncCount);
        Xunit.Assert.Equal(1, repository.TransactGoalCount);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_refresh_dispatches_goal_flag_uses_requested_goal_scoped_state")]
    public async Task PersistentRunnerRefreshDispatchesGoalFlagUsesRequestedGoalScopedState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var currentTask = new TaskSpec(TaskId.New(), "Current work", AgentRole.Developer);
        var targetTask = new TaskSpec(TaskId.New(), "Target work", AgentRole.Developer);
        var current = kernel.CreateGoal("Current refresh", [currentTask]);
        var target = kernel.CreateGoal("Target refresh", [targetTask]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = current;
        kernel.ActivateGoal(current.Id, agents);
        kernel.ActivateGoal(target.Id, agents);
        RecordRunningProcess(kernel, target, targetTask, root);
        File.WriteAllText(targetTask.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(targetTask.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["refresh-dispatches", "--goal", target.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Equal(1, repository.TransactionCount);
        Xunit.Assert.Equal(1, repository.SaveGoalSnapshotsCount);
        Xunit.Assert.Equal([target.Id.Value], repository.LoadedGoalIds);
        Xunit.Assert.Equal(target.Id, currentGoal!.Id);

        var currentSnapshot = await repository.LoadGoalAsync(current.Id);
        var targetSnapshot = await repository.LoadGoalAsync(target.Id);
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([currentSnapshot!, targetSnapshot!], []));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, restored.GetTask(current.Id, currentTask.Id).Status);
        var restoredTargetTask = restored.GetTask(target.Id, targetTask.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restoredTargetTask.Status);
        Xunit.Assert.Equal(0, restoredTargetTask.LastProcess!.ExitCode);
        Xunit.Assert.NotNull(restoredTargetTask.LastVerification);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_reconcile_discards_stale_process_identity")]
    public async Task PersistentRunnerReconcileDiscardsStaleProcessIdentity()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Stale reconcile", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.BeforeNextTransaction = stored =>
        {
            var current = stored.GetTask(goal.Id, task.Id);
            var replacement = current.LastProcess! with
            {
                ProcessId = 424242,
                StartedAt = current.LastProcess.StartedAt.AddSeconds(1),
                ExitCodePath = current.LastProcess.ExitCodePath + ".next"
            };
            stored.RecordTaskProcessRefreshed(goal.Id, task.Id, replacement, null);
        };

        var changed = false;
        CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["reconcile"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var restoredTask = (await repository.LoadAsync()).GetTask(goal.Id, task.Id);
        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(WorkTaskStatus.Running, restoredTask.Status);
        Xunit.Assert.Equal(424242, restoredTask.LastProcess!.ProcessId);
        Xunit.Assert.Null(restoredTask.LastProcess.ExitCode);
        Xunit.Assert.Equal(1, repository.TransactionCount);
    }


}
