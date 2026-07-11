using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsTerminalSweepCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_with_assigned_task_reopens_goal_idempotently")]
    public void TerminalGoalSweepCompletedWithAssignedTaskReopensGoalIdempotently()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Premature completion", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

        Xunit.Assert.True(first.Changed);
        var repair = first.Goals.Single().Repairs.Single(repair => repair.Kind == "terminal-task-desync");
        Xunit.Assert.Contains("goalState=Completed", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal($"conduct {goal.Id.Value[..8]} --loop", repair.Command);
        Xunit.Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Empty(second.Goals);
    }


    [Xunit.Theory(DisplayName = "TerminalGoalSweep_global_stale_terminal_goal_with_assigned_task_is_excluded_once")]
    [Xunit.InlineData(GoalStatus.Completed)]
    [Xunit.InlineData(GoalStatus.Cancelled)]
    [Xunit.InlineData(GoalStatus.Failed)]
    public void TerminalGoalSweepGlobalStaleTerminalGoalWithAssignedTaskIsExcludedOnce(GoalStatus status)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal($"Stale {status}", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, status);

        var first = TerminalGoalSweep.Run(kernel, root);
        var second = TerminalGoalSweep.Run(kernel, root);

        Xunit.Assert.False(first.Changed);
        var result = first.Goals.Single();
        Xunit.Assert.Empty(result.Repairs);
        var blocker = result.Blockers.Single();
        Xunit.Assert.Equal("stale-terminal-excluded", blocker.Kind);
        Xunit.Assert.Equal("excluded", blocker.Command);
        Xunit.Assert.Contains($"goalId={goal.Id.Value}", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"goalState={status}", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains("action=excluded", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal(status, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        Xunit.Assert.Single(second.Goals.Single().Blockers);
        Xunit.Assert.Equal(1, first.ExcludedGoalCount);
        Xunit.Assert.Equal(1, second.ExcludedGoalCount);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_excluded_goals_print_one_summary_line")]
    public void TerminalGoalSweepExcludedGoalsPrintOneSummaryLine()
    {
        var kernel = new AgentOrchestratorKernel();
        for (var i = 0; i < 3; i++)
        {
            var task = new TaskSpec(TaskId.New(), $"Do work {i}", AgentRole.Developer);
            var goal = kernel.CreateGoal($"Stale {i}", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        }

        var result = TerminalGoalSweep.Run(kernel, CreateTempDirectory());
        var output = CaptureConsole(() => ConsoleViews.PrintTerminalGoalSweep(result));

        Xunit.Assert.Equal(3, result.ExcludedGoalCount);
        Xunit.Assert.Equal(1, CountLinesContaining(output, "SWEEP_SUMMARY kind=stale-terminal-excluded count=3"));
        Xunit.Assert.Equal(0, CountLinesContaining(output, "SWEEP_BLOCKER"));
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_verified_missing_branch_reachable_from_main_reconciles_to_cleaned_up")]
    public void TerminalGoalSweepVerifiedMissingBranchReachableFromMainReconcilesToCleanedUp()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Verified missing branch already landed", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktree = GoalWorktrees.Ensure(root, goal.Id);
            var baseCommit = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            var path = Path.Combine(worktree, "src", "landed-missing.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "goal work");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Goal work");
            var resultCommit = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "manual",
                "manual",
                worktree,
                DateTimeOffset.UtcNow,
                BaseCommit: baseCommit,
                ResultCommit: resultCommit));
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                worktree,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            _ = GoalWorktrees.Remove(root, goal.Id);

            var first = TerminalGoalSweep.Run(kernel, root);
            var second = TerminalGoalSweep.Run(kernel, root);
            var facts = new GoalLifecycleFacts(
                WorkspaceExists: GoalWorktrees.TryResolve(root, goal.Id) is not null,
                IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(root, goal.Id)),
                IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(GoalOperationJournal.Read(root, goal.Id)),
                IsCleanedUp: GoalOperationJournal.HasCompletedCleanupEvidence(GoalOperationJournal.Read(root, goal.Id)));

            Xunit.Assert.True(first.Changed);
            Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "missing-branch-landed-reconciled");
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.Equal(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(kernel.GetGoal(goal.Id), facts));
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_verified_missing_branch_integration_commit_reconciles_to_landed")]
    public void TerminalGoalSweepVerifiedMissingBranchIntegrationCommitReconcilesToLanded()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Verified missing branch with integration commit", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            File.WriteAllText(Path.Combine(root, "operator-bridged.txt"), "operator landed");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", $"Operator bridged landing for goal {goal.Id.Value[..8]}");

            var first = TerminalGoalSweep.Run(kernel, root);
            var repair = first.Goals.Single().Repairs.Single();

            Xunit.Assert.Equal("missing-branch-landed-reconciled", repair.Kind);
            Xunit.Assert.Contains("reachableIntegrationCommit=", repair.Evidence, StringComparison.Ordinal);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.True(GoalOperationJournal.HasCompletedCleanupEvidence(GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_verified_missing_branch_dogfood_log_reconciles_to_landed")]
    public async Task TerminalGoalSweepVerifiedMissingBranchDogfoodLogReconcilesToLanded()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Verified missing branch with dogfood log", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            var workspace = CreateRefinedWorkspace(root);
            await new DogfoodLogStore(workspace.DogfoodLogStorePath).UpsertAsync(new DogfoodLogAppend(
                goal.Id.Value,
                "Landed operator-bridged goal",
                "Landed via operator bridge.",
                "passed",
                "manual",
                $"Landed goal {goal.Id.Value[..8]} via operator bridge."));

            var first = TerminalGoalSweep.Run(kernel, root);
            var repair = first.Goals.Single().Repairs.Single();

            Xunit.Assert.Equal("missing-branch-landed-reconciled", repair.Kind);
            Xunit.Assert.Contains("dogfoodLogSequence=", repair.Evidence, StringComparison.Ordinal);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.True(GoalOperationJournal.HasCompletedCleanupEvidence(GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_verified_missing_branch_unverifiable_is_retired_once")]
    public void TerminalGoalSweepVerifiedMissingBranchUnverifiableIsRetiredOnce()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Verified missing branch unverifiable", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));

            var first = TerminalGoalSweep.Run(kernel, root);
            var second = TerminalGoalSweep.Run(kernel, root);

            Xunit.Assert.True(first.Changed);
            var repair = first.Goals.Single().Repairs.Single(repair => repair.Kind == "missing-branch-retired");
            Xunit.Assert.Contains("record retired", repair.Evidence, StringComparison.Ordinal);
            Xunit.Assert.Equal("retired", repair.Command);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.True(GoalOperationJournal.HasCompletedCleanupEvidence(GoalOperationJournal.Read(root, goal.Id)));
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_global_stale_terminal_reconciles_human_input_exit_before_exclusion")]
    public void TerminalGoalSweepGlobalStaleTerminalReconcilesHumanInputExitBeforeExclusion()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Need operator", AgentRole.Developer);
        var goal = kernel.CreateGoal("Terminal process should stay terminal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "HUMAN_INPUT: choose a recovery path");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var result = TerminalGoalSweep.Run(kernel, root);

        var goalResult = result.Goals.Single();
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Contains(goalResult.Repairs, repair => repair.Kind == "dispatch-exit-reconciled");
        Xunit.Assert.Empty(goalResult.Blockers);
        Xunit.Assert.Equal(GoalStatus.WaitingForHuman, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, kernel.GetTask(goal.Id, task.Id).Status);
        Xunit.Assert.False(kernel.GetTask(goal.Id, task.Id).LastProcess!.IsRunning);
        Xunit.Assert.Equal(0, kernel.GetTask(goal.Id, task.Id).LastProcess!.ExitCode);
        Xunit.Assert.Single(kernel.HumanInputRequests);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_superseded_with_assigned_task_reopens_goal")]
    public void TerminalGoalSweepSupersededWithAssignedTaskReopensGoal()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Premature superseded", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Superseded);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

        Xunit.Assert.True(first.Changed);
        var repair = first.Goals.Single().Repairs.Single(repair => repair.Kind == "terminal-task-desync");
        Xunit.Assert.Contains("goalState=Superseded", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Empty(second.Goals);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_with_assigned_task_after_retry_reopens_goal")]
    public void TerminalGoalSweepCompletedWithAssignedTaskAfterRetryReopensGoal()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retry stale terminal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed before retry");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        kernel.RetryTask(goal.Id, task.Id, "retry after stale terminal completion");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

        var repair = first.Goals.Single().Repairs.Single(repair => repair.Kind == "terminal-task-desync");
        Xunit.Assert.Contains("goalState=Completed", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", repair.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Empty(second.Goals);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_terminal_running_task_with_live_process_blocks_without_reopening")]
    public void TerminalGoalSweepTerminalRunningTaskWithLiveProcessBlocksWithoutReopening()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Live dispatch terminal desync", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        RecordRunningProcess(kernel, goal, task, root, Environment.ProcessId);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

        Xunit.Assert.False(first.Changed);
        var blocker = first.Goals.Single().Blockers.Single(blocker => blocker.Kind == "terminal-live-dispatch");
        Xunit.Assert.Contains("goalState=Completed", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Running", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"pid={Environment.ProcessId}", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal($"refresh-dispatch {goal.Id.Value[..8]} 1", blocker.Command);
        Xunit.Assert.Empty(first.Goals.Single().Repairs);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
        Xunit.Assert.False(second.Changed);
        Xunit.Assert.Contains(second.Goals.Single().Blockers, blocker => blocker.Kind == "terminal-live-dispatch");
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_assigned_task_with_dirty_worktree_blocks_without_reopening")]
    public void TerminalGoalSweepCompletedAssignedTaskWithDirtyWorktreeBlocksWithoutReopening()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Dirty terminal desync", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

        Xunit.Assert.False(first.Changed);
        var blocker = first.Goals.Single().Blockers.Single(blocker => blocker.Kind == "terminal-dirty-worktree");
        Xunit.Assert.Contains("goalState=Completed", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains("worktreeDirty=true", blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Contains(worktree, blocker.Evidence, StringComparison.Ordinal);
        Xunit.Assert.Equal($"goal-recovery {goal.Id.Value[..8]}", blocker.Command);
        Xunit.Assert.Empty(first.Goals.Single().Repairs);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
        Xunit.Assert.False(second.Changed);
        Xunit.Assert.Contains(second.Goals.Single().Blockers, blocker => blocker.Kind == "terminal-dirty-worktree");
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_with_terminal_tasks_is_clean_and_idempotent")]
    public void TerminalGoalSweepCompletedWithTerminalTasksIsCleanAndIdempotent()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Valid terminal goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        var before = kernel.ExportSnapshot();

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var after = kernel.ExportSnapshot();

        Xunit.Assert.Empty(first.Goals);
        Xunit.Assert.Empty(second.Goals);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_next_repairs_but_global_conduct_excludes_terminal_task_desync")]
    public void TerminalGoalSweepNextRepairsButGlobalConductExcludesTerminalTaskDesync()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Completed assigned desync", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var nextOutput = CaptureConsole(() =>
        {
            var nextRepository = new InMemoryTransactionalStateRepository(kernel);
            CliPersistentStateRunner.ExecuteCommand(
                ["next", goal.Id.Value[..8]],
                nextRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        agents = AgentCatalog.Default().Agents;
        profiles = WorkerProfileCatalog.Default();
        currentGoal = goal;
        var conductOutput = CaptureConsole(() =>
        {
            var conductRepository = new InMemoryTransactionalStateRepository(kernel);
            CliPersistentStateRunner.ExecuteCommand(
                ["conduct", "--loop", "--max-iterations", "1"],
                conductRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        var nextRepair = SingleLineContaining(nextOutput, "SWEEP_REPAIR");
        Xunit.Assert.Contains("kind=terminal-task-desync", nextRepair, StringComparison.Ordinal);
        Xunit.Assert.Contains("goalState=Completed", nextRepair, StringComparison.Ordinal);
        Xunit.Assert.Contains($"{task.Id.Value[..8]}:Assigned", nextRepair, StringComparison.Ordinal);
        Xunit.Assert.Contains($"command=\"conduct {goal.Id.Value[..8]} --loop\"", nextRepair, StringComparison.Ordinal);

        var conductExclusion = SingleLineContaining(conductOutput, "SWEEP_SUMMARY");
        Xunit.Assert.Contains("kind=stale-terminal-excluded", conductExclusion, StringComparison.Ordinal);
        Xunit.Assert.Contains("count=1", conductExclusion, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("SWEEP_BLOCKER", conductOutput, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("SWEEP_REPAIR", conductOutput, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_readiness_prints_repair_before_preflight")]
    public void TerminalGoalSweepReadinessPrintsRepairBeforePreflight()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Readiness desync", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var changed = false;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(
            ["readiness", goal.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var repairIndex = output.IndexOf("SWEEP_REPAIR", StringComparison.Ordinal);
        var readinessIndex = output.IndexOf("Goal readiness", StringComparison.Ordinal);
        Xunit.Assert.True(changed);
        Xunit.Assert.True(repairIndex >= 0, output);
        Xunit.Assert.True(readinessIndex > repairIndex, output);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_running_task_with_exit_file_reconciles_and_is_idempotent")]
    public void TerminalGoalSweepCompletedRunningTaskWithExitFileReconcilesAndIsIdempotent()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Running exit completion", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var second = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var sweptTask = kernel.GetTask(goal.Id, task.Id);

        Xunit.Assert.True(first.Changed);
        Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "dispatch-exit-reconciled");
        Xunit.Assert.Equal(WorkTaskStatus.Completed, sweptTask.Status);
        Xunit.Assert.Equal(0, sweptTask.LastProcess!.ExitCode);
        Xunit.Assert.Empty(second.Goals);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_global_completed_running_task_with_exit_file_reconciles_before_exclusion")]
    public void TerminalGoalSweepGlobalCompletedRunningTaskWithExitFileReconcilesBeforeExclusion()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Global running exit completion", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        RecordRunningProcess(kernel, goal, task, root);
        File.WriteAllText(task.LastProcess!.ExitCodePath, "0");
        File.WriteAllText(task.LastProcess.StandardOutputPath, "done");
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var first = TerminalGoalSweep.Run(kernel, root);
        var second = TerminalGoalSweep.Run(kernel, root);
        var sweptTask = kernel.GetTask(goal.Id, task.Id);

        Xunit.Assert.True(first.Changed);
        Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "dispatch-exit-reconciled");
        Xunit.Assert.Empty(first.Goals.Single().Blockers);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, sweptTask.Status);
        Xunit.Assert.Equal(0, sweptTask.LastProcess!.ExitCode);
        Xunit.Assert.Empty(second.Goals);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_next_and_conduct_surface_same_unmerged_branch_blocker")]
    public void TerminalGoalSweepNextAndConductSurfaceSameUnmergedBranchBlocker()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Completed but unmerged", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/unmerged.txt", "goal work");
            var workspace = CreateRefinedWorkspace(root);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var nextOutput = CaptureConsole(() =>
            {
                var nextRepository = new InMemoryTransactionalStateRepository(kernel);
                CliPersistentStateRunner.ExecuteCommand(
                    ["next", goal.Id.Value[..8]],
                    nextRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            agents = AgentCatalog.Default().Agents;
            profiles = WorkerProfileCatalog.Default();
            currentGoal = goal;
            var diagnosticsOutput = CaptureConsole(() =>
            {
                var diagnosticsRepository = new InMemoryTransactionalStateRepository(kernel);
                CliPersistentStateRunner.ExecuteCommand(
                    ["next", goal.Id.Value[..8], "--full"],
                    diagnosticsRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            agents = AgentCatalog.Default().Agents;
            profiles = WorkerProfileCatalog.Default();
            currentGoal = goal;
            var conductOutput = CaptureConsole(() =>
            {
                var conductRepository = new InMemoryTransactionalStateRepository(kernel);
                CliPersistentStateRunner.ExecuteCommand(
                    ["conduct", "--loop", "--max-iterations", "1"],
                    conductRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            var expected = $"SWEEP_BLOCKER goal={goal.Id.Value[..8]} kind=completed-branch-unmerged";
            var command = $"command=\"acceptance {goal.Id.Value[..8]}\"";
            Xunit.Assert.Contains(expected, nextOutput);
            Xunit.Assert.Contains(command, nextOutput);
            Xunit.Assert.Contains(expected, conductOutput);
            Xunit.Assert.Contains(command, conductOutput);
            Xunit.Assert.Equal(1, CountLinesContaining(conductOutput, expected));
            Xunit.Assert.Contains("Goal diagnostics", diagnosticsOutput);
            Xunit.Assert.Contains(expected, diagnosticsOutput);
            Xunit.Assert.Contains(command, diagnosticsOutput);
            Xunit.Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_raw_completed_assigned_task_with_unmerged_branch_reopens_without_acceptance_blocker")]
    public void TerminalGoalSweepRawCompletedAssignedTaskWithUnmergedBranchReopensWithoutAcceptanceBlocker()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Raw completed assigned with unmerged branch", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            CommitGoalWork(root, goal.Id, "src/raw-completed-assigned.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var result = TerminalGoalSweep.Run(kernel, root, goal.Id);
            var goalResult = Assert.Single(result.Goals);

            Assert.Contains(goalResult.Repairs, repair => repair.Kind == "terminal-task-desync");
            Assert.DoesNotContain(goalResult.Blockers, blocker => blocker.Kind == "completed-branch-unmerged");
            Assert.Empty(goalResult.Blockers);
            Assert.Equal(GoalStatus.Active, kernel.GetGoal(goal.Id).Status);
            Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));
            Assert.False(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_conduct_loop_early_exits_print_blocker")]
    public void TerminalGoalSweepConductLoopEarlyExitsPrintBlocker()
    {
        var cases = new[]
        {
            new { Name = "max-iterations-zero", Args = new[] { "conduct", "--loop", "--max-iterations", "0" }, StopFile = false },
            new { Name = "zero-duration", Args = new[] { "conduct", "--loop", "--max-duration", "0" }, StopFile = false },
            new { Name = "stop-file", Args = new[] { "conduct", "--loop" }, StopFile = true }
        };

        foreach (var testCase in cases)
        {
            var root = CreateAcceptanceRepository();
            GoalId? cleanupGoalId = null;
            try
            {
                var kernel = new AgentOrchestratorKernel();
                var task = new TaskSpec(TaskId.New(), $"Do work for {testCase.Name}", AgentRole.Developer);
                var goal = kernel.CreateGoal($"Completed but unmerged {testCase.Name}", [task]);
                cleanupGoalId = goal.Id;
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
                CommitGoalWork(root, goal.Id, $"src/{testCase.Name}.txt", "goal work");
                if (testCase.StopFile)
                {
                    File.WriteAllText(Path.Combine(root, ConductorBatchLoop.StopFileName), "stop");
                }

                var workspace = CreateRefinedWorkspace(root);
                IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
                var providers = new InMemoryModelProviderRegistry([]);
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = goal;

                var output = CaptureConsole(() =>
                {
                    var repository = new InMemoryTransactionalStateRepository(kernel);
                    CliPersistentStateRunner.ExecuteCommand(
                        testCase.Args,
                        repository,
                        workspace,
                        ref agents,
                        providers,
                        ref profiles,
                        ref currentGoal);
                });

                var expected = $"SWEEP_BLOCKER goal={goal.Id.Value[..8]} kind=completed-branch-unmerged";
                Xunit.Assert.Contains(expected, output);
                Xunit.Assert.Equal(1, CountLinesContaining(output, expected));
            }
            finally
            {
                CleanupAcceptanceRepository(root, cleanupGoalId);
            }
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_merged_branch_cleans_worktree_and_branch")]
    public void TerminalGoalSweepCompletedMergedBranchCleansWorktreeAndBranch()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Completed and merged", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/merged.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

            var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
            var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

            Xunit.Assert.True(first.Changed);
            Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "merged-branch-cleanup");
            Xunit.Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(string.Empty, RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_incomplete_completed_worktree_cleanup_reports_blocker")]
    public void TerminalGoalSweepIncompleteCompletedWorktreeCleanupReportsBlocker()
    {
        var root = CreateAcceptanceRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Completed merged with leftover cleanup", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            var worktree = CommitGoalWork(root, goal.Id, "src/leftover.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            File.Delete(Path.Combine(worktree, ".git"));
            RunGit(root, "worktree", "prune");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new NoOpSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = TerminalGoalSweep.Run(kernel, root, goal.Id);
            var goalResult = Assert.Single(result.Goals);
            var blocker = Assert.Single(goalResult.Blockers);

            Assert.False(result.Changed);
            Assert.Empty(goalResult.Repairs);
            Assert.Equal("completed-worktree-cleanup-needed", blocker.Kind);
            Assert.True(blocker.Evidence.Contains("leftover directory cleanup is incomplete", StringComparison.OrdinalIgnoreCase));
            Assert.Equal($"conduct {goal.Id.Value[..8].ToLowerInvariant()} --loop", blocker.Command);
            Assert.True(Directory.Exists(worktree));
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_completed_assigned_task_with_merged_branch_cleans_without_reopening")]
    public void TerminalGoalSweepCompletedAssignedTaskWithMergedBranchCleansWithoutReopening()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Completed assigned but landed", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            CommitGoalWork(root, goal.Id, "src/landed.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var first = TerminalGoalSweep.Run(kernel, root, goal.Id);
            var second = TerminalGoalSweep.Run(kernel, root, goal.Id);

            Xunit.Assert.True(first.Changed);
            Xunit.Assert.DoesNotContain(first.Goals.Single().Repairs, repair => repair.Kind == "terminal-task-desync");
            Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "landed-task-desync");
            Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "merged-branch-cleanup");
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.Equal(WorkTaskStatus.Cancelled, kernel.GetTask(goal.Id, task.Id).Status);
            Xunit.Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_batches_git_branch_facts_once_per_sweep")]
    public void TerminalGoalSweepBatchesGitBranchFactsOncePerSweep()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        var originalRunner = TerminalGoalSweep.GitRunner;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 3; index++)
            {
                var task = new TaskSpec(TaskId.New(), $"Do work {index}", AgentRole.Developer);
                var goal = kernel.CreateGoal($"Completed unmerged {index}", [task]);
                cleanupGoalId ??= goal.Id;
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
                CommitGoalWork(root, goal.Id, $"src/batched-{index}.txt", "goal work");
                kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            }

            var batchedGitCalls = new List<string>();
            TerminalGoalSweep.GitRunner = (workingDirectory, args) =>
            {
                if (Path.GetFullPath(workingDirectory).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
                    args.Count > 0 &&
                    (args[0] == "for-each-ref" || args.SequenceEqual(["worktree", "list", "--porcelain"])))
                {
                    batchedGitCalls.Add(string.Join(" ", args));
                }

                return originalRunner(workingDirectory, args);
            };

            var result = TerminalGoalSweep.Run(kernel, root);

            Assert.Equal(3, result.Goals.Count);
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "worktree list --porcelain"));
        }
        finally
        {
            TerminalGoalSweep.GitRunner = originalRunner;
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


}
