using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);

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

        var first = RunSweep(kernel, root);
        var second = RunSweep(kernel, root);

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

        var result = RunSweep(kernel, CreateTempDirectory());
        var output = CaptureConsole(() => ConsoleViews.PrintTerminalGoalSweep(result));

        Xunit.Assert.Equal(3, result.ExcludedGoalCount);
        Xunit.Assert.Equal(1, CountLinesContaining(output, "SWEEP_SUMMARY kind=stale-terminal-excluded count=3"));
        Xunit.Assert.Equal(0, CountLinesContaining(output, "SWEEP_BLOCKER"));
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweepAttention_raises_one_remediation_item_and_resolves_when_blocker_clears")]
    public async Task TerminalGoalSweepAttentionRaisesOneRemediationItemAndResolvesWhenBlockerClears()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Completed branch needs acceptance", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            $"completed goal still has unmerged branch {GoalWorktrees.BranchName(goal.Id)}",
            $"acceptance {goal.Id.Value[..8]}");
        var result = new TerminalGoalSweepResult([
            new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])
        ]);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);

        await TerminalGoalSweepAttention.SurfaceAsync(kernel, result, store);
        await TerminalGoalSweepAttention.SurfaceAsync(kernel, result, store);

        var raised = await store.ListAsync(goal.Id.Value);
        var item = Xunit.Assert.Single(raised);
        Xunit.Assert.Equal(CollaborationItemType.Decision, item.Type);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Xunit.Assert.Contains("completed-branch-unmerged", item.Subject, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Goal: {goal.Id.Value[..8]}", item.Body, StringComparison.Ordinal);
        Xunit.Assert.Contains("SWEEP_BLOCKER", item.Body, StringComparison.Ordinal);
        Xunit.Assert.Contains(blocker.Evidence, item.Body, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Command: acceptance {goal.Id.Value[..8]}", item.Body, StringComparison.Ordinal);

        await TerminalGoalSweepAttention.SurfaceAsync(kernel, new TerminalGoalSweepResult([], CacheHitCount: 1), store);

        var stillRaised = (await store.ListAsync(goal.Id.Value)).Single();
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, stillRaised.Status);

        await TerminalGoalSweepAttention.SurfaceAsync(kernel, new TerminalGoalSweepResult([], SweptGoalIds: [goal.Id]), store);

        var resolved = (await store.ListAsync(goal.Id.Value)).Single();
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.Equal("terminal sweep blocker resolved", resolved.Resolution);
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_merged_branch_without_integrate_subject_stays_verified")]
    public async Task TerminalGoalSweepMergedBranchWithoutIntegrateSubjectStaysVerified()
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

            var first = RunSweep(kernel, root);
            var attentionStore = CollaborationItemStore.ForDirectory(
                OrchestratorWorkspace.ForDirectory(root).OrchestratorDirectory);
            var raisedAttention = await TerminalGoalSweepAttention.SurfaceAsync(kernel, first, attentionStore, goal.Id);
            var second = RunSweep(kernel, root);
            var blocker = Xunit.Assert.Single(Xunit.Assert.Single(first.Goals).Blockers);
            Xunit.Assert.Equal("verified-merged-branch-missing-integrate-commit", blocker.Kind);
            Xunit.Assert.Equal(1, raisedAttention);
            Xunit.Assert.Single(await attentionStore.GetAttentionQueueAsync());
            Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.NotEqual(string.Empty, RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
            Xunit.Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(root, goal.Id)));
            Xunit.Assert.Contains(second.Blockers, item => item.Kind == "verified-merged-branch-missing-integrate-commit");
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_arbitrary_goal_commit_without_integrate_subject_stays_verified")]
    public void TerminalGoalSweepArbitraryGoalCommitWithoutIntegrateSubjectStaysVerified()
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

            var first = RunSweep(kernel, root);
            Xunit.Assert.Empty(first.Goals);
            Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_dogfood_log_without_integrate_subject_stays_verified")]
    public async Task TerminalGoalSweepDogfoodLogWithoutIntegrateSubjectStaysVerified()
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

            var first = RunSweep(kernel, root);
            Xunit.Assert.Empty(first.Goals);
            Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_missing_branch_without_integrate_subject_stays_verified")]
    public void TerminalGoalSweepMissingBranchWithoutIntegrateSubjectStaysVerified()
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

            var first = RunSweep(kernel, root);
            var second = RunSweep(kernel, root);

            Xunit.Assert.False(first.Changed);
            Xunit.Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(root, goal.Id)));
            Xunit.Assert.Empty(first.Goals);
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_standing_retired_disposition_skips_repair_and_escalation")]
    public void TerminalGoalSweepStandingRetiredDispositionSkipsRepairAndEscalation()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retired ghost goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        GoalOperationJournal.RecordTerminalDisposition(
            root,
            goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "goal-mark-landed retired ghost goal"));

        var result = RunSweep(kernel, root, goal.Id);

        Xunit.Assert.Empty(result.Goals);
        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_conduct_loop_suppresses_retired_disposition_output")]
    public void TerminalGoalSweepConductLoopSuppressesRetiredDispositionOutput()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Retired conductor ghost goal", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
        GoalOperationJournal.RecordTerminalDisposition(
            root,
            goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "goal-mark-landed retired ghost goal"));

        var output = CaptureConsole(() =>
        {
            var repository = new InMemoryTransactionalStateRepository(kernel);
            CliPersistentStateRunner.ExecuteCommand(
                ["conduct", "--loop", "--max-iterations", "1"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.DoesNotContain("SWEEP_REPAIR", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("SWEEP_BLOCKER", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("acceptance " + goal.Id.Value[..8], output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("conduct " + goal.Id.Value[..8] + " --loop", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_branch_reappearance_supersedes_retired_disposition")]
    public void TerminalGoalSweepBranchReappearanceSupersedesRetiredDisposition()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Retired branch reappears", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            GoalOperationJournal.RecordTerminalDisposition(
                root,
                goal,
                new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "operator retired missing goal branch"));
            CommitGoalWork(root, goal.Id, "src/reappeared.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var result = RunSweep(kernel, root, goal.Id);
            var goalResult = Assert.Single(result.Goals);

            Assert.Contains(goalResult.Repairs, repair => repair.Kind == "completed-branch-normalized");
            var blocker = Assert.Single(goalResult.Blockers);
            Assert.Equal("completed-branch-unmerged", blocker.Kind);
            Assert.Equal($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public void CompletedEquivalentBranch_IsRetiredWithoutLandingBlocker()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Equivalent branch already upstream", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/equivalent.txt", "already upstream");
            var branch = GoalWorktrees.BranchName(goal.Id);
            var mainPath = Path.Combine(root, "src", "equivalent.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath)!);
            File.WriteAllText(mainPath, "already upstream");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Equivalent work landed by another goal");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            Assert.False(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
            Assert.StartsWith("- ", RunGitOutput(root, "cherry", "main", branch).Trim(), StringComparison.Ordinal);
            var diagnosis = DiagnoseSweep(kernel, root, goal.Id);
            var diagnosed = Assert.Single(diagnosis.Blockers);
            Assert.Equal("completed-branch-superseded", diagnosed.Kind);
            Assert.Equal($"conduct {goal.Id.Value[..8]} --loop", diagnosed.Command);

            var first = RunSweep(kernel, root, goal.Id);
            var second = RunSweep(kernel, root, goal.Id);

            var firstGoal = Assert.Single(first.Goals);
            Assert.Contains(firstGoal.Repairs, repair => repair.Kind == "completed-branch-superseded");
            Assert.Empty(firstGoal.Blockers);
            Assert.DoesNotContain(first.Blockers, blocker => blocker.Kind == "completed-branch-unmerged");
            Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Assert.Equal(string.Empty, RunGitOutput(root, "branch", "--list", branch).Trim());
            Assert.True(GoalOperationJournal.HasRetiredTerminalDisposition(
                GoalOperationJournal.Read(root, goal.Id)));
            Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public void VerifiedEquivalentBranch_IsNamedSupersededWithoutAutoRetirement()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Verified equivalent branch already upstream", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/verified-equivalent.txt", "already upstream");
            var branch = GoalWorktrees.BranchName(goal.Id);
            var mainPath = Path.Combine(root, "src", "verified-equivalent.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath)!);
            File.WriteAllText(mainPath, "already upstream");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Equivalent verified work landed by another goal");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Verified);

            Assert.False(GoalWorktrees.IsBranchMergedIntoCurrent(root, goal.Id));
            Assert.StartsWith("- ", RunGitOutput(root, "cherry", "main", branch).Trim(), StringComparison.Ordinal);

            var diagnosis = DiagnoseSweep(kernel, root, goal.Id);
            var run = RunSweep(kernel, root, goal.Id);

            Assert.Equal("completed-branch-superseded", Assert.Single(diagnosis.Blockers).Kind);
            var blocker = Assert.Single(Assert.Single(run.Goals).Blockers);
            Assert.Equal("completed-branch-superseded", blocker.Kind);
            Assert.Equal($"conduct {goal.Id.Value[..8]} --loop", blocker.Command);
            Assert.NotEqual(string.Empty, RunGitOutput(root, "branch", "--list", branch).Trim());
            Assert.False(GoalOperationJournal.HasRetiredTerminalDisposition(
                GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public void CompletedSquashedBranchWithAbsentPatchIds_RemainsLandingBlocker()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Squashed branch is inconclusive", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            var worktree = CommitGoalWork(root, goal.Id, "src/squashed-a.txt", "first");
            File.WriteAllText(Path.Combine(worktree, "src", "squashed-b.txt"), "second");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Second goal commit");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "squashed-a.txt"), "first");
            File.WriteAllText(Path.Combine(root, "src", "squashed-b.txt"), "second");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Squashed equivalent landing");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var cherry = RunGitOutput(root, "cherry", "main", GoalWorktrees.BranchName(goal.Id));
            var cherryLines = cherry.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(cherryLines);
            Assert.All(
                cherryLines,
                line => Assert.StartsWith("+ ", line, StringComparison.Ordinal));

            var result = RunSweep(kernel, root, goal.Id);
            var blocker = Assert.Single(Assert.Single(result.Goals).Blockers);

            Assert.Equal("completed-branch-unmerged", blocker.Kind);
            Assert.Equal($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.NotEqual(
                string.Empty,
                RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public void CompletedBranchWithInconclusiveCherry_RemainsLandingBlocker()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Inconclusive patch equivalence", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/inconclusive.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            GitCli.GitResult GitRunner(string workingDirectory, IReadOnlyList<string> args) =>
                args.Count > 0 && args[0] == "cherry"
                    ? new GitCli.GitResult(0, "ambiguous output", string.Empty)
                    : StableGitRunner(workingDirectory, args);

            var diagnosis = DiagnoseSweep(kernel, root, goal.Id, GitRunner);
            var result = RunSweep(kernel, root, goal.Id, gitRunner: GitRunner);

            var diagnosed = Assert.Single(diagnosis.Blockers);
            Assert.Equal("completed-branch-unmerged", diagnosed.Kind);
            Assert.Contains("contentCheck=inconclusive", diagnosed.Evidence, StringComparison.Ordinal);
            var blocker = Assert.Single(Assert.Single(result.Goals).Blockers);
            Assert.Equal("completed-branch-unmerged", blocker.Kind);
            Assert.Contains("contentCheck=inconclusive", blocker.Evidence, StringComparison.Ordinal);
            Assert.Equal($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.NotEqual(
                string.Empty,
                RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
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

        var result = RunSweep(kernel, root);

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);

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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);
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

        var first = RunSweep(kernel, root, goal.Id);
        var second = RunSweep(kernel, root, goal.Id);
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

        var first = RunSweep(kernel, root);
        var second = RunSweep(kernel, root);
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
            var attention = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync()
                .GetAwaiter()
                .GetResult();
            var item = Xunit.Assert.Single(attention);
            Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
            Xunit.Assert.Contains("completed-branch-unmerged", item.Subject, StringComparison.Ordinal);
            Xunit.Assert.Contains(expected, item.Body, StringComparison.Ordinal);
            Xunit.Assert.Contains($"Command: acceptance {goal.Id.Value[..8]}", item.Body, StringComparison.Ordinal);
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

            var result = RunSweep(kernel, root, goal.Id);
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

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_matching_gate_artifact_produces_typed_acceptance_remedy")]
    public void TerminalGoalSweepMatchingGateArtifactProducesTypedAcceptanceRemedy()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Typed acceptance remedy", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/typed-remedy.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Verified);
            var branchHead = RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim();
            var mainHead = RunGitOutput(root, "rev-parse", "main").Trim();
            GoalOperationJournal.AcceptanceGatePassed(
                root,
                goal,
                "acceptance",
                branchHead,
                mainHead,
                "terminal gate passed");

            var blocker = Assert.Single(DiagnoseSweep(kernel, root, goal.Id).Blockers);

            Assert.Equal(TerminalGoalRemedyVerb.Acceptance, blocker.Remedy.Verb);
            Assert.Equal(goal.Id, blocker.Remedy.GoalId);
            Assert.Equal(branchHead, blocker.Remedy.GateArtifact?.CandidateBranchSha);
            Assert.Equal($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.Equal(["acceptance", goal.Id.Value], blocker.Remedy.BuildInvocationArguments());
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_acceptance_guard_abort_is_operator_only_and_not_a_bare_retry")]
    public void TerminalGoalSweepAcceptanceGuardAbortIsOperatorOnlyAndNotABareRetry()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Do not repeat an aborted acceptance gate", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/aborted-remedy.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Verified);
            var branchHead = RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim();
            var mainHead = RunGitOutput(root, "rev-parse", "main").Trim();
            var gateAt = DateTimeOffset.Parse("2026-08-09T03:30:00Z");
            GoalOperationJournal.AcceptanceGatePassed(
                root,
                goal,
                "acceptance",
                branchHead,
                mainHead,
                "terminal gate passed",
                gateAt);
            GoalOperationJournal.AcceptanceAborted(
                root,
                goal,
                "acceptance",
                branchHead,
                mainHead,
                "Acceptance aborted:state-guard: Task task-one.Status expected Completed, actual Running.",
                gateAt.AddMinutes(1));

            var blocker = Assert.Single(DiagnoseSweep(kernel, root, goal.Id).Blockers);

            Assert.Equal(TerminalGoalRemedyVerb.OperatorCommand, blocker.Remedy.Verb);
            Assert.Equal(TerminalGoalRemedySafetyClass.OperatorOnly, blocker.Remedy.SafetyClass);
            Assert.Null(blocker.Remedy.GateArtifact);
            Assert.Contains("Quiesce conductor mutations", blocker.Command);
            Assert.Contains($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.NotEqual($"acceptance {goal.Id.Value[..8]}", blocker.Command);
            Assert.Empty(blocker.Remedy.BuildInvocationArguments());
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact(DisplayName = "TerminalGoalSweep_acceptance_guard_abort_without_gate_does_not_claim_receipt")]
    public void TerminalGoalSweepAcceptanceGuardAbortWithoutGateDoesNotClaimReceipt()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Report an abort without a passing gate", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/aborted-without-gate.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Verified);
            var branchHead = RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim();
            var mainHead = RunGitOutput(root, "rev-parse", "main").Trim();
            GoalOperationJournal.AcceptanceAborted(
                root,
                goal,
                "acceptance",
                branchHead,
                mainHead,
                "Acceptance aborted:state-guard before verification.");

            var acceptance = GoalAcceptanceStatusProjector.Build(kernel, kernel.GetGoal(goal.Id), root);
            var acceptanceAbort = Assert.Single(acceptance.Blockers.Where(candidate => candidate.Kind == GoalAcceptanceBlockerKind.AcceptanceAborted));
            Assert.Contains("no passing gate receipt was recorded", acceptanceAbort.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            var blocker = Assert.Single(DiagnoseSweep(kernel, root, goal.Id).Blockers);

            Assert.Equal(TerminalGoalRemedyVerb.OperatorCommand, blocker.Remedy.Verb);
            Assert.Contains("no passing gate receipt was recorded", blocker.Command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("receipt none remains recorded", blocker.Command, StringComparison.OrdinalIgnoreCase);
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
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                root,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                ProviderSessionId: "terminal-sweep-session",
                WorktreeHeadSha: "abc123",
                DirtyStateHash: "dirty-hash"));
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/merged.txt", "goal work");
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var first = RunSweep(kernel, root, goal.Id);
            var second = RunSweep(kernel, root, goal.Id);

            Xunit.Assert.True(first.Changed);
            Xunit.Assert.Contains(first.Goals.Single().Repairs, repair => repair.Kind == "merged-branch-cleanup");
            Xunit.Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Xunit.Assert.Equal(string.Empty, RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
            Xunit.Assert.Equal("terminal-sweep-session", kernel.GetTask(goal.Id, task.Id).LastDispatch!.ProviderSessionId);
            Xunit.Assert.NotNull(kernel.GetTask(goal.Id, task.Id).LastDispatch!.ProviderSessionRetiredAt);
            Xunit.Assert.Empty(second.Goals);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_merged_branch_without_landing_intent_auto_repairs_then_cleans_idempotently")]
    public void TerminalGoalSweepMergedBranchWithoutLandingIntentAutoRepairsThenCleansIdempotently()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Merged without durable landing", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/transient-merged.txt", "goal work");
            var expectedMergeSha = RunGitOutput(root, "log", "--format=%H", "-n", "1", GoalWorktrees.BranchName(goal.Id)).Trim();
            RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            var cache = new TerminalGoalSweepCache();
            var injectedGitCalls = new List<string>();
            GitCli.GitResult GitRunner(string workingDirectory, IReadOnlyList<string> args)
            {
                injectedGitCalls.Add(string.Join(' ', args));
                return StableGitRunner(workingDirectory, args);
            }

            var first = RunSweep(kernel, root, goal.Id, cache, gitRunner: GitRunner);

            var firstRepair = Assert.Single(first.Goals.Single().Repairs);
            Assert.Equal("landing-intent-auto-repair", firstRepair.Kind);
            Assert.Contains($"goalId={goal.Id.Value}", firstRepair.Evidence, StringComparison.Ordinal);
            Assert.Contains($"mergeCommitSha={expectedMergeSha}", firstRepair.Evidence, StringComparison.Ordinal);
            Assert.Contains("source=auto-repair", firstRepair.Evidence, StringComparison.Ordinal);
            Assert.Contains(injectedGitCalls, call => call.StartsWith("log --format=%H --reverse --ancestry-path ", StringComparison.Ordinal));
            Assert.Empty(first.Goals.Single().Blockers);
            Assert.Equal(0, first.CacheHitCount);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.NotNull(GoalWorktrees.TryResolve(root, goal.Id));

            var second = RunSweep(kernel, root, goal.Id, cache, gitRunner: GitRunner);
            var secondRepairs = second.Goals.Single().Repairs;
            Assert.Contains(secondRepairs, repair => repair.Kind == "landing-intent-auto-repair-noop");
            Assert.Contains(secondRepairs, repair => repair.Kind == "merged-branch-cleanup");
            Assert.Empty(second.Goals.Single().Blockers);
            Assert.Equal(0, second.CacheHitCount);
            Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Assert.Equal(string.Empty, RunGitOutput(root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());

            var third = RunSweep(kernel, root, goal.Id, cache, gitRunner: GitRunner);
            Assert.Empty(third.Goals);

            var journal = GoalOperationJournal.Read(root, goal.Id);
            var intents = journal.Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Completed).ToArray();
            var intent = Assert.Single(intents);
            Assert.Contains($"\"mergeCommitSha\":\"{expectedMergeSha}\"", intent.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(journal.Entries, entry =>
                entry.AcceptanceOutcome == "merged-branch-without-landing-intent");
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }


    [Xunit.Fact(DisplayName = "TerminalGoalSweep_auto_repair_recovers_merge_sha_from_main_when_sweeping_off_main")]
    public void TerminalGoalSweepAutoRepairRecoversMergeShaFromMainWhenSweepingOffMain()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Merged without durable landing from off-main checkout", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/off-main-merged.txt", "goal work");
            var goalBranch = GoalWorktrees.BranchName(goal.Id);

            RunGit(root, "checkout", "-b", "side");
            RunGit(root, "merge", "--no-ff", goalBranch, "-m", "Side merge");
            var sideMergeSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            RunGit(root, "checkout", "main");
            RunGit(root, "merge", "--no-ff", goalBranch, "-m", "Main merge");
            var mainMergeSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            RunGit(root, "checkout", "side");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var result = RunSweep(kernel, root, goal.Id);

            var repair = Assert.Single(result.Goals.Single().Repairs);
            Assert.Equal("landing-intent-auto-repair", repair.Kind);
            Assert.Contains($"mergeCommitSha={mainMergeSha}", repair.Evidence, StringComparison.Ordinal);
            Assert.DoesNotContain($"mergeCommitSha={sideMergeSha}", repair.Evidence, StringComparison.Ordinal);
            var intent = Assert.Single(GoalOperationJournal.Read(root, goal.Id).Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Completed));
            Assert.Contains($"\"mergeCommitSha\":\"{mainMergeSha}\"", intent.Detail, StringComparison.Ordinal);
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
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            File.Delete(Path.Combine(worktree, ".git"));
            RunGit(root, "worktree", "prune");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var cleanupHooks = StableCleanupHooks with
            {
                DeleteDirectory = _ => false,
                ResetSandboxAcl = (_, _) => { },
                BuildServerShutdown = (_, _) => { }
            };

            var result = RunSweep(kernel, root, goal.Id, cleanupHooks: cleanupHooks);
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
            GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

            var first = RunSweep(kernel, root, goal.Id);
            var second = RunSweep(kernel, root, goal.Id);

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

    [Xunit.Fact]
    public async Task Run_MergeEvidence_TerminalizesAndResolvesGoalAttention()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var backlogItem = await backlogStore.AddAsync("Keep source backlog unchanged");
        var kernel = new AgentOrchestratorKernel();
        kernel.SetEventWriter(new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
        var task = new TaskSpec(TaskId.New(), "Do landed work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Merged goal", [task]);
        var other = kernel.CreateGoal("Still-live goal", [new TaskSpec(TaskId.New(), "Keep working", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(goal.Id, backlogItem.Id);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(other.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);

        await store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Decision", "body", "merged-decision");
        await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, "Clarification", "body", "merged-clarification");
        await store.RaiseAsync(CollaborationItemType.Verify, other.Id.Value, "Other verify", "body", "other-verify");
        var sentinelWorktree = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(sentinelWorktree);
        File.WriteAllText(Path.Combine(sentinelWorktree, "sentinel.txt"), "keep");
        var evidence = new GoalIntegrationEvidence("integrate-sha", "main-sha", $"Integrate goal/{goal.Id.Value[..8]}");
        var resolver = new StubGoalIntegrationEvidenceResolver(goal.Id, evidence);

        var first = RunSweep(
            kernel,
            root,
            goal.Id,
            integrationEvidenceResolver: resolver,
            attentionStore: store);
        var newlyRaisedOrResolved = await TerminalGoalSweepAttention.SurfaceAsync(kernel, first, store, goal.Id);
        var second = RunSweep(
            kernel,
            root,
            goal.Id,
            integrationEvidenceResolver: resolver,
            attentionStore: store);

        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, task.Id).Status);
        Xunit.Assert.Equal(1, first.TerminalizedGoalCount);
        Xunit.Assert.Equal(2, first.ResolvedAttentionItemCount);
        Xunit.Assert.Equal(0, newlyRaisedOrResolved);
        Xunit.Assert.Empty(Xunit.Assert.Single(first.Goals).Blockers);
        Xunit.Assert.Equal(0, second.TerminalizedGoalCount);
        Xunit.Assert.Equal(0, second.ResolvedAttentionItemCount);
        Xunit.Assert.Equal(1, resolver.TargetCalls);
        Xunit.Assert.True(File.Exists(Path.Combine(sentinelWorktree, "sentinel.txt")));
        Xunit.Assert.Equal(BacklogItemStatus.Open, (await backlogStore.GetByExactIdAsync(backlogItem.Id))!.Status);

        var open = await store.GetAttentionQueueAsync();
        Xunit.Assert.Single(open);
        Xunit.Assert.Equal(other.Id.Value, open[0].GoalId);
        var resolved = await store.ListAsync(goal.Id.Value);
        Xunit.Assert.Equal(2, resolved.Count(item => item.Status == CollaborationItemStatus.Resolved));
        Xunit.Assert.All(resolved, item => Xunit.Assert.Contains("goal terminalized from merge evidence at integrate-sha", item.Resolution));
        var journal = GoalOperationJournal.Read(root, goal.Id);
        Xunit.Assert.Contains(journal.Entries, entry =>
            entry.Operation == GoalOperationJournal.TerminalDispositionOperation &&
            entry.Detail.Contains("\"kind\":\"Landed\"", StringComparison.Ordinal));
        var eventLines = File.ReadAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl"));
        Xunit.Assert.Single(eventLines.Where(line => line.Contains("\"eventType\":\"GoalLanded\"", StringComparison.Ordinal)));
        Xunit.Assert.Contains(eventLines, line => line.Contains("\"source\":\"merge-evidence\"", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Run_MergeEvidenceWithCancelledTask_RecordsConflict()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review landed work", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Merged despite cancelled review", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Cancelled.");
        var evidence = new GoalIntegrationEvidence("integrate-sha", "main-sha", $"Integrate goal/{goal.Id.Value[..8]}");

        var result = RunSweep(
            kernel,
            root,
            goal.Id,
            integrationEvidenceResolver: new StubGoalIntegrationEvidenceResolver(goal.Id, evidence));

        Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        Xunit.Assert.Equal(1, result.TerminalizedGoalCount);
        Xunit.Assert.Contains(goal.Timeline, item =>
            item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains($"task={task.Id.Value} status=Cancelled", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Verifying)]
    [Xunit.InlineData(GoalStatus.Verified)]
    public async Task ReachableIntegrateCommitTerminalizesWithoutGoalBranch(GoalStatus status)
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var backlogStore = new BacklogStore(workspace.BacklogStorePath);
            var backlogItem = await backlogStore.AddAsync($"Ancestry landing {status}");
            var kernel = new AgentOrchestratorKernel();
            kernel.SetEventWriter(new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal($"Ancestry-derived landing from {status}", [task]);
            cleanupGoalId = goal.Id;
            kernel.SetGoalSourceBacklogItemId(goal.Id, backlogItem.Id);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            if (status == GoalStatus.Verifying)
            {
                kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance started before external merge.");
            }

            CommitGoalWork(root, goal.Id, $"src/ancestry-{status}.txt", "goal work");
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            var branchTip = RunGitOutput(root, "rev-parse", goalBranch).Trim();
            RunGit(root, "branch", "side", "main");
            RunGit(root, "merge", "--no-ff", goalBranch, "-m", $"Integrate {goalBranch}");
            var integrateSha = RunGitOutput(root, "rev-parse", "refs/heads/main").Trim();
            RunGit(root, "revert", "-m", "1", integrateSha, "--no-edit");
            var mainSha = RunGitOutput(root, "rev-parse", "refs/heads/main").Trim();
            RunGit(root, "checkout", "side");
            RunGit(root, "worktree", "remove", "--force", GoalWorktrees.WorktreePath(root, goal.Id));
            RunGit(root, "branch", "-D", goalBranch);
            Xunit.Assert.NotEqual(0, GitCli.Run(root, "rev-parse", "--verify", $"refs/heads/{goalBranch}").ExitCode);
            Xunit.Assert.Equal(0, GitCli.Run(root, "merge-base", "--is-ancestor", integrateSha, mainSha).ExitCode);

            TerminalGoalSweepResult? result = null;
            var incidentalOutput = CaptureConsole(() => result = RunSweep(kernel, root, goal.Id));

            var goalResult = Xunit.Assert.Single(Xunit.Assert.IsType<TerminalGoalSweepResult>(result).Goals);
            Xunit.Assert.DoesNotContain("Closed backlog item", incidentalOutput, StringComparison.Ordinal);
            Xunit.Assert.Contains(goalResult.Repairs, repair => repair.Kind == "merge-evidence-terminalized");
            Xunit.Assert.Empty(goalResult.Blockers);
            Xunit.Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            var journal = GoalOperationJournal.Read(root, goal.Id);
            var terminalDispositionEntry = Xunit.Assert.Single(journal.Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.TerminalDispositionOperation));
            Xunit.Assert.Contains("\"kind\":\"Landed\"", terminalDispositionEntry.Detail, StringComparison.Ordinal);
            Xunit.Assert.Contains($"merge evidence at {integrateSha}", terminalDispositionEntry.Detail, StringComparison.Ordinal);

            var unchangedBacklogItem = await backlogStore.GetByExactIdAsync(backlogItem.Id);
            Xunit.Assert.Equal(BacklogItemStatus.Open, unchangedBacklogItem!.Status);
            Xunit.Assert.Null(await new DogfoodLogStore(workspace.DogfoodLogStorePath)
                .GetByGoalIdAsync(goal.Id.Value));

            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var landedLine = File.ReadLines(eventsPath).Single(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("eventType").GetString() == "GoalLanded";
            });
            using var landedEvent = JsonDocument.Parse(landedLine);
            Xunit.Assert.Equal(goalBranch, landedEvent.RootElement.GetProperty("goalBranch").GetString());
            Xunit.Assert.Equal(integrateSha, landedEvent.RootElement.GetProperty("integrateSha").GetString());
            Xunit.Assert.Equal(mainSha, landedEvent.RootElement.GetProperty("mainSha").GetString());
            Xunit.Assert.Equal("merge-evidence", landedEvent.RootElement.GetProperty("source").GetString());
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public async Task VerifyingUnmergedBranchDoesNotRaiseDecision()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Ordinary mid-acceptance goal", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual",
                root,
                0,
                "passed",
                string.Empty,
                DateTimeOffset.UtcNow));
            kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance in progress.");
            CommitGoalWork(root, goal.Id, "src/verifying-unmerged.txt", "goal work");
            var branchTip = RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim();
            Xunit.Assert.NotEqual(0, GitCli.Run(root, "merge-base", "--is-ancestor", branchTip, "refs/heads/main").ExitCode);

            var result = RunSweep(kernel, root, goal.Id);
            await TerminalGoalSweepAttention.SurfaceAsync(
                kernel,
                result,
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
            var output = CaptureConsole(() => ConsoleViews.PrintTerminalGoalSweep(result));

            Xunit.Assert.Empty(result.Blockers);
            Xunit.Assert.DoesNotContain("SWEEP_BLOCKER", output, StringComparison.Ordinal);
            Xunit.Assert.Empty(await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync());
            Xunit.Assert.Equal(GoalStatus.Verifying, kernel.GetGoal(goal.Id).Status);
            Xunit.Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(
                GoalOperationJournal.Read(root, goal.Id)));
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
            GitCli.GitResult GitRunner(string workingDirectory, IReadOnlyList<string> args)
            {
                if (Path.GetFullPath(workingDirectory).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
                    args.Count > 0 &&
                    (args[0] is "for-each-ref" or "rev-parse" || args.SequenceEqual(["worktree", "list", "--porcelain"])))
                {
                    batchedGitCalls.Add(string.Join(" ", args));
                }

                return StableGitRunner(workingDirectory, args);
            }

            var result = RunSweep(kernel, root, gitRunner: GitRunner);

            Assert.Equal(3, result.Goals.Count);
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) %(objectname) refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "worktree list --porcelain"));
            Assert.Equal(1, batchedGitCalls.Count(call => call == "rev-parse --verify refs/heads/main"));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }
    [Xunit.Fact]
    public void Resolver_RepeatedGoalAtSameMain_ProbesAncestryOnce()
    {
        var root = CreateTempDirectory();
        var goalId = GoalId.New();
        var ancestryCalls = 0;
        GitCli.GitResult GitRunner(string _, IReadOnlyList<string> args)
        {
            var command = string.Join(' ', args);
            if (args.Count > 0 && args[0] == "log")
            {
                return new GitCli.GitResult(
                    0,
                    $"integrate-sha\tIntegrate goal/{goalId.Value[..8]}\n",
                    string.Empty);
            }

            if (command == "merge-base --is-ancestor integrate-sha main-sha")
            {
                ancestryCalls++;
                return new GitCli.GitResult(0, string.Empty, string.Empty);
            }

            return new GitCli.GitResult(1, string.Empty, $"unexpected git command: {command}");
        }
        var resolver = GoalIntegrationEvidenceResolver.Build(root, "main-sha", GitRunner);

        Xunit.Assert.True(resolver.TryResolve(goalId, out var first));
        Xunit.Assert.True(resolver.TryResolve(goalId, out var second));

        Xunit.Assert.Equal(first, second);
        Xunit.Assert.Equal(1, ancestryCalls);
    }

    [Xunit.Fact]
    public void MergeEvidenceTerminalizationDefersDestructiveCleanupUntilNextSweep()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Merged goal with cleanup artifacts", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/deferred-merge-cleanup.txt", "goal work");
            var branch = GoalWorktrees.BranchName(goal.Id);
            RunGit(root, "merge", "--no-ff", branch, "-m", $"Integrate {branch}");
            var worktree = GoalWorktrees.WorktreePath(root, goal.Id);

            var first = RunSweep(kernel, root, goal.Id);

            Xunit.Assert.Equal(1, first.TerminalizedGoalCount);
            Xunit.Assert.True(Directory.Exists(worktree));
            Xunit.Assert.NotEqual(string.Empty, RunGitOutput(root, "branch", "--list", branch).Trim());

            var second = RunSweep(kernel, root, goal.Id);

            Xunit.Assert.Contains(second.Goals.SelectMany(item => item.Repairs), repair =>
                repair.Kind == "merged-branch-cleanup");
            Xunit.Assert.False(Directory.Exists(worktree));
            Xunit.Assert.Equal(string.Empty, RunGitOutput(root, "branch", "--list", branch).Trim());
            Xunit.Assert.True(GoalOperationJournal.HasMergeEvidenceTerminalDisposition(
                GoalOperationJournal.Read(root, goal.Id)));
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    [Xunit.Fact]
    public async Task Run_MixedGoals_LeavesNoTerminalGoalInAttentionQueue()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var kernel = new AgentOrchestratorKernel();
        var terminalTask = new TaskSpec(TaskId.New(), "Completed work", AgentRole.Developer);
        var terminalGoal = kernel.CreateGoal("Already terminal", [terminalTask]);
        var activeGoal = kernel.CreateGoal("Still active", [new TaskSpec(TaskId.New(), "Active work", AgentRole.Developer)]);
        kernel.ActivateGoal(terminalGoal.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(activeGoal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(terminalGoal.Id, terminalTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(terminalGoal.Id, terminalTask.Id, new TaskVerificationRecord(
            "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        kernel.CompleteGoal(terminalGoal.Id, "Already completed.");
        await store.RaiseAsync(CollaborationItemType.Decision, terminalGoal.Id.Value, "Terminal decision", "body", "terminal-decision");
        await store.RaiseAsync(CollaborationItemType.Clarification, activeGoal.Id.Value, "Active clarification", "body", "active-clarification");
        var neverMatchedGoal = GoalId.New();
        var resolver = new StubGoalIntegrationEvidenceResolver(
            neverMatchedGoal,
            new GoalIntegrationEvidence("unused", "main-sha", $"Integrate goal/{neverMatchedGoal.Value[..8]}"));

        var result = RunSweep(
            kernel,
            root,
            integrationEvidenceResolver: resolver,
            attentionStore: store);

        Xunit.Assert.Equal(1, result.ResolvedAttentionItemCount);
        var open = await store.GetAttentionQueueAsync();
        Xunit.Assert.Single(open);
        Xunit.Assert.Equal(activeGoal.Id.Value, open[0].GoalId);
        Xunit.Assert.DoesNotContain(open, item => item.GoalId == terminalGoal.Id.Value);
    }

    [Xunit.Fact]
    public async Task Run_BackfillExceedsBound_TerminalizesNoneAndRaisesOneBlocker()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var kernel = new AgentOrchestratorKernel();
        for (var index = 0; index <= TerminalGoalSweep.MaxMergeEvidenceTerminalizationsPerSweep; index++)
        {
            var task = new TaskSpec(TaskId.New(), $"Do work {index}", AgentRole.Developer);
            var goal = kernel.CreateGoal($"Merged goal {index}", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
        }

        var result = RunSweep(
            kernel,
            root,
            integrationEvidenceResolver: new AlwaysIntegratedEvidenceResolver(),
            attentionStore: store);
        await TerminalGoalSweepAttention.SurfaceAsync(kernel, result, store);

        var blocker = Xunit.Assert.Single(result.Blockers.Where(item =>
            item.Kind == "merge-evidence-backfill-bound-exceeded"));
        Xunit.Assert.Equal("merge-evidence-backfill-bound-exceeded", blocker.Kind);
        Xunit.Assert.Equal(0, result.TerminalizedGoalCount);
        Xunit.Assert.Equal(kernel.Goals.Count, result.ExplicitlySweptGoalIds.Count);
        Xunit.Assert.All(kernel.Goals, goal => Xunit.Assert.Equal(GoalStatus.Verified, goal.Status));
        Xunit.Assert.All(kernel.Goals.SelectMany(goal => goal.Tasks), task => Xunit.Assert.Null(task.LastDispatch));
        Xunit.Assert.Single(await store.GetAttentionQueueAsync());
    }

    private sealed class StubGoalIntegrationEvidenceResolver(
        GoalId targetGoalId,
        GoalIntegrationEvidence evidence) : IGoalIntegrationEvidenceResolver
    {
        public int TargetCalls { get; private set; }

        public bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? resolvedEvidence)
        {
            if (goalId == targetGoalId)
            {
                TargetCalls++;
                resolvedEvidence = evidence;
                return true;
            }

            resolvedEvidence = null;
            return false;
        }
    }

    private sealed class AlwaysIntegratedEvidenceResolver : IGoalIntegrationEvidenceResolver
    {
        public bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? evidence)
        {
            evidence = new GoalIntegrationEvidence(
                $"integrate-{goalId.Value[..8]}",
                "main-sha",
                $"Integrate goal/{goalId.Value[..8]}");
            return true;
        }
    }

    private static readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> StableGitRunner =
        static (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());

    private static readonly GoalWorktreeCleanupHooks StableCleanupHooks = new();

    private static TerminalGoalSweepResult RunSweep(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null,
        TerminalGoalSweepCache? cache = null,
        IGoalIntegrationEvidenceResolver? integrationEvidenceResolver = null,
        ICollaborationItemStore? attentionStore = null,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null,
        GoalWorktreeCleanupHooks? cleanupHooks = null) =>
        TerminalGoalSweep.Run(
            kernel,
            executionDirectory,
            onlyGoalId,
            cache,
            integrationEvidenceResolver,
            attentionStore,
            gitRunner ?? StableGitRunner,
            cleanupHooks ?? StableCleanupHooks);

    private static TerminalGoalSweepResult DiagnoseSweep(
        AgentOrchestratorKernel kernel,
        string executionDirectory,
        GoalId? onlyGoalId = null,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null) =>
        TerminalGoalSweep.Diagnose(kernel, executionDirectory, onlyGoalId, gitRunner ?? StableGitRunner);


}
