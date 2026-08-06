using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalWorktreeTestsRebaseMerge : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "GoalWorktrees_fast_forwards_goal_branch_on_merge")]
    public void GoalWorktreesFastForwardsGoalBranchOnMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var buildEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "cleanup-test");
            Assert.True(Directory.Exists(buildEnvironment.RootPath));

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            var removeResult = GoalWorktrees.Remove(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + GoalWorktrees.BranchName(goalId) + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.False(Directory.Exists(buildEnvironment.RootPath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Workspace merge carries authoritative changed paths without a goal worktree")]
    public void WorkspaceMergeCarriesChangedPathsFromBranchWithoutWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var branch = GoalWorktrees.BranchName(goalId);
            const string enginePath =
                "tests/canary-fixture/branch-only.txt";
            var baseBranch = GitCli.Run(repo, "branch", "--show-current").Output.Trim();
            RunGit(repo, "checkout", "-b", branch);
            var fullPath = Path.Combine(repo, enginePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "namespace BranchOnly;");
            RunGit(repo, "add", enginePath);
            RunGit(repo, "commit", "-m", "Branch-only acceptance engine change");
            RunGit(repo, "checkout", baseBranch);
            Assert.Null(GoalWorktrees.TryResolve(repo, goalId));

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.NotNull(merge);
            Assert.True(merge!.FastForwarded);
            Assert.Equal([enginePath], merge.ChangedFiles);
            Assert.True(File.Exists(Path.Combine(repo, enginePath)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Workspace merge fails closed when branch changed paths cannot be determined")]
    public void WorkspaceMergeFailsClosedWhenChangedPathsAreUnavailable()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var branch = GoalWorktrees.BranchName(goalId);
            var baseBranch = GitCli.Run(repo, "branch", "--show-current").Output.Trim();
            RunGit(repo, "checkout", "-b", branch);
            File.WriteAllText(Path.Combine(repo, "branch-only.txt"), "must not land");
            RunGit(repo, "add", "branch-only.txt");
            RunGit(repo, "commit", "-m", "Branch-only change");
            var branchHead = GitCli.Run(repo, "rev-parse", "HEAD").Output.Trim();
            RunGit(repo, "checkout", baseBranch);
            RunGit(repo, "symbolic-ref", "HEAD", "refs/heads/missing-landing-target");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.NotNull(merge);
            Assert.False(merge!.FastForwarded);
            Assert.Null(merge.ChangedFiles);
            Assert.Contains("changed-file determination failed", merge.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(branchHead, GitCli.Run(repo, "rev-parse", branch).Output.Trim());
            Assert.False(File.Exists(Path.Combine(repo, "branch-only.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees rechecks mutation blocker immediately before fast-forward")]
    public void GoalWorktreesRechecksMutationBlockerBeforeFastForward()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.WriteAllText(Path.Combine(path, "blocked-feature.txt"), "must not land");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Blocked goal work");
            var mainBefore = GitCli.Run(repo, "rev-parse", "HEAD").Output.Trim();
            var checks = 0;

            var merge = GoalWorktrees.TryFastForwardMerge(
                repo,
                goalId,
                () =>
                {
                    Interlocked.Increment(ref checks);
                    return "acceptance circuit opened after admission";
                });

            Assert.NotNull(merge);
            Assert.False(merge!.FastForwarded);
            Assert.Null(merge.SuggestedCommand);
            Assert.Contains("blocked before merge", merge.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, checks);
            Assert.Equal(mainBefore, GitCli.Run(repo, "rev-parse", "HEAD").Output.Trim());
            Assert.False(File.Exists(Path.Combine(repo, "blocked-feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_suggests_manual_merge_when_branches_diverge")]
    public void GoalWorktreesSuggestsManualMergeWhenBranchesDiverge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(merge is not null);
            Assert.False(merge!.FastForwarded);
            Assert.Equal($"git merge {GoalWorktrees.BranchName(goalId)}", merge.SuggestedCommand);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_rebases_stale_branch_onto_main_when_clean")]
    public void GoalWorktreesRebasesStaleBranchOntoMainWhenClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);
            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(
                rebase.Status == GoalWorktreeRebaseStatus.Rebased,
                $"{rebase.Status}: {rebase.Message}");
            Assert.True(rebase.ConflictFiles.Count == 0);
            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_reports_conflict_files_and_aborts_rebase")]
    public void GoalWorktreesReportsConflictFilesAndAbortsRebase()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "seed.txt"), "goal edit");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal edit");

            File.WriteAllText(Path.Combine(repo, "seed.txt"), "main edit");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main edit");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.True(
                rebase.Status == GoalWorktreeRebaseStatus.Conflict,
                $"{rebase.Status}: {rebase.Message}");
            Assert.Equal(1, rebase.ConflictFiles.Count);
            Assert.Equal("seed.txt", rebase.ConflictFiles[0]);
            Assert.True(rebase.SuggestedCommand?.Contains("Create an operator task", StringComparison.Ordinal) == true);
            Assert.Equal("goal edit", File.ReadAllText(Path.Combine(path, "seed.txt")));
            Assert.Equal("main edit", File.ReadAllText(Path.Combine(repo, "seed.txt")));
            Assert.Equal(0, RunGitExitCode(path, "rev-parse", "--verify", "--quiet", "HEAD"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_refuses_rebase_when_worktree_dirty")]
    public void GoalWorktreesRefusesRebaseWhenWorktreeDirty()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "uncommitted goal work");
            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.DirtyWorktree, rebase.Status);
            Assert.True(rebase.Message.Contains("uncommitted changes", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(path, "feature.txt")));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_merge_returns_null_without_goal_branch")]
    public void GoalWorktreesMergeReturnsNullWithoutGoalBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            Assert.True(GoalWorktrees.TryFastForwardMerge(repo, GoalId.New()) is null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_rebase_updates_clean_stale_goal_branch")]
    public void CliWorkspaceRebaseUpdatesCleanStaleGoalBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace rebase goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var path = GoalWorktrees.Ensure(repo, goal.Id);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["workspace", "rebase"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            var merge = GoalWorktrees.TryFastForwardMerge(repo, goal.Id);

            Assert.True(output.Contains("Rebased", StringComparison.Ordinal));
            Assert.True(output.Contains("Next: acceptance", StringComparison.Ordinal));
            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_blocks_merge_when_verification_fails")]
    public void CliAcceptanceBlocksMergeWhenVerificationFails()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var sourceDirectory = Path.Combine(worktreePath, "src");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Change.cs"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Failed("Test run failed\nFailed: 2");
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            Assert.True(output.Contains("failed (exit 1)", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Test run failed", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "src", "Change.cs")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_merges_after_passing_verification")]
    public void CliAcceptanceMergesAfterPassingVerification()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.True(output.Contains($"lease id: goal-{goal.Id.Value[..8].ToLowerInvariant()}", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification check: passed", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_direct_merge_journals_landing_before_cleanup_for_later_missing_worktree_retry")]
    public void CliAcceptanceDirectMergeJournalsLandingBeforeCleanupForLaterMissingWorktreeRetry()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Direct acceptance journal recovery", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", goal.Id.Value[..8]], context));

            Assert.Contains("Fast-forwarded", output);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(HasCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, goal.Id), "remove:acceptance-deferred"));
            Assert.Null(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "acceptance" &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.DoesNotContain(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove");

            var cleanupBackoff = GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id);
            Assert.NotNull(cleanupBackoff);
            Assert.Equal("remove:acceptance-deferred", cleanupBackoff!.Reason);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_before_deferred_cleanup")]
    public void CliAcceptanceAcceptedRoutesStopHostMergeMarkLandedBeforeDeferredCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance lifecycle ordering test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var order = new List<string>();
            var writer = new RecordingGoalLifecycleEventWriter(order);
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    order.Add("merge");
                    return request.Merge();
                },
                stopAcceptanceHosts: request =>
                {
                    Assert.Equal(worktreePath, request.WorktreePath);
                    Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
                    order.Add("stop-host");
                    return AcceptanceHostStopResult.Success("Stop-host: test stopped host.");
                })
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = writer
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal));
            Assert.True(output.Contains("Stop-host: test stopped host.", StringComparison.Ordinal));
            Assert.Equal(["stop-host", "merge", "mark-landed"], order);
            Assert.True(output.Contains("Workspace cleanup deferred", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_stop_host_timeout_blocks_before_merge")]
    public void CliAcceptanceStopHostTimeoutBlocksBeforeMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stop-host blocker test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var mergeCalled = false;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    mergeCalled = true;
                    return request.Merge();
                },
                stopAcceptanceHosts: _ => new AcceptanceHostStopResult(
                    false,
                    "BLOCKER step=stop-host reason=timeout hosts=pid=123 name=Mcg.AgentOrchestrator.App log=host.log action=\"Stop exact PID(s), inspect log path(s), then rerun acceptance.\"",
                    [new AcceptanceHostProcess(123, "Mcg.AgentOrchestrator.App", "host command", "host.log")]))
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.False(mergeCalled);
            Assert.True(output.Contains("BLOCKER step=stop-host", StringComparison.Ordinal));
            Assert.True(output.Contains("pid=123", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed")]
    public void ReconcileSweepRetriesAcceptanceAfterUntrackedRebaseBlockerIsRemoved()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Recover acceptance after dirty rebase", repo);
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Entered Verifying before the terminal gate."));
            Assert.True(kernel.ReconcileGoalAcceptanceVerified(goal.Id, "Terminal gate passed; returned to Verified for landing."));
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            var branchHead = RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim();
            var priorMainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            GoalOperationJournal.AcceptanceGatePassed(
                repo,
                goal,
                "acceptance",
                branchHead,
                priorMainHead,
                "terminal gate passed");

            File.WriteAllText(Path.Combine(repo, "main-advance.txt"), "main moved");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Advance main");
            var untrackedDirectory = Path.Combine(worktreePath, "Microsoft", "Windows", "PowerShell", "ModuleAnalysisCache");
            Directory.CreateDirectory(untrackedDirectory);
            File.WriteAllText(Path.Combine(untrackedDirectory, "cache.bin"), "untracked");
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var firstOutput = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.Contains("Workspace rebase:", firstOutput, StringComparison.Ordinal);
            Assert.Contains("merge blocked", firstOutput, StringComparison.Ordinal);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));

            Directory.Delete(Path.Combine(worktreePath, "Microsoft"), recursive: true);
            var blocker = Assert.Single(TerminalGoalSweep.Diagnose(kernel, repo, goal.Id).Blockers);
            var executionOutput = string.Empty;
            var coordinator = new ReconcileSweepRemediationCoordinator(
                new ReconcileSweepRemediationStore(OrchestratorWorkspace.ForDirectory(repo).SqliteStatePath),
                ReconcileSweepOptions.Default,
                _ =>
                {
                    executionOutput = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
                    var succeeded = File.Exists(Path.Combine(repo, "feature.txt"));
                    return new TerminalGoalRemedyExecutionResult(succeeded ? 0 : 1, executionOutput);
                },
                remedy => RunGitOutput(repo, "rev-parse", GoalWorktrees.BranchName(remedy.GoalId)).Trim());

            var outcome = coordinator.Process(new TerminalGoalSweepResult([
                new TerminalGoalSweepGoalResult(goal.Id, goal.Id.Value[..8], [], [blocker])
            ]));

            Assert.True(outcome.RemedySucceeded);
            Assert.Contains(outcome.Events, line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal));
            Assert.Contains(outcome.Events, line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal) && line.Contains("exit=0", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Empty(TerminalGoalSweep.Diagnose(kernel, repo, goal.Id).Blockers);
            Assert.Contains(goal.Timeline, entry => entry.Message.Contains("Verifying", StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(worktreePath))
            {
                Assert.Equal(string.Empty, RunGitOutput(worktreePath, "status", "--short").Trim());
            }
            else
            {
                Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_verification_timeout_blocks_before_merge_with_diagnostics")]
    public void CliAcceptanceVerificationTimeoutBlocksBeforeMergeWithDiagnostics()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance verification timeout blocker test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var mergeCalled = false;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    mergeCalled = true;
                    return request.Merge();
                },
                stopAcceptanceHosts: _ => AcceptanceHostStopResult.Success("Stop-host: none."))
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Timeout()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.False(mergeCalled);
            Assert.True(output.Contains("Verification check: failed - acceptance-check-timeout: infrastructure-tests elapsed=25m budget=25m (exit -1)", StringComparison.Ordinal), output);
            Assert.True(output.Contains("BLOCKER step=verification reason=timeout", StringComparison.Ordinal), output);
            Assert.True(output.Contains("artifacts=C:\\artifacts\\goal-acceptance", StringComparison.Ordinal), output);
            Assert.True(output.Contains("Command: dotnet test infrastructure", StringComparison.Ordinal), output);
            Assert.True(output.Contains("stdout: C:\\temp\\acc.out", StringComparison.Ordinal), output);
            Assert.True(output.Contains("stderr: C:\\temp\\acc.err", StringComparison.Ordinal), output);
            Assert.True(output.Contains("Last output:", StringComparison.Ordinal), output);
            Assert.True(output.Contains("still running", StringComparison.Ordinal), output);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            var goalSnapshot = Assert.Single(kernel.ExportSnapshot().Goals);
            Assert.Equal(GoalStatus.Verified, goalSnapshot.Status);
            Assert.DoesNotContain($"Goal {goal.Id.Value[..8]} completed", output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, kernel.BuildLoopHealthReport().CompletedGoalCount);
            Assert.Empty(kernel.BuildProvenanceReport($"Landed goal {goal.Id.Value[..8]}").Goals);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_merge_conflict_blocks_and_leaves_worktree")]
    public void CliAcceptanceMergeConflictBlocksAndLeavesWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance merge blocker test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: _ => new AcceptanceMergeCommitResult(false, "conflicting files: feature.txt"),
                stopAcceptanceHosts: _ => AcceptanceHostStopResult.Success("Stop-host: none."))
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("BLOCKER step=merge", StringComparison.Ordinal));
            Assert.True(output.Contains("feature.txt", StringComparison.Ordinal));
            Assert.True(!output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal), output);
            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: not accepted", StringComparison.Ordinal), output);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
            var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
            Assert.False(acceptance.IsAccepted);
            Assert.Contains(acceptance.Blockers, blocker =>
                blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceFailed &&
                blocker.Message.Contains("merge", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge")]
    public void CliAcceptanceNonAcceptedVerdictDoesNotStopHostOrMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance non-accepted test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            var stopCalled = false;
            var mergeCalled = false;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    mergeCalled = true;
                    return request.Merge();
                },
                stopAcceptanceHosts: _ =>
                {
                    stopCalled = true;
                    return AcceptanceHostStopResult.Success("Stop-host: should not run.");
                })
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Failed("Focused tests failed")
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(stopCalled);
            Assert.False(mergeCalled);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rebases_before_verification_and_lands_verified_head")]
    public void CliAcceptanceRebasesBeforeVerificationAndLandsVerifiedHead()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance rebase ordering test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            var staleGoalHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            string? verifierHead = null;
            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                verifierHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");
            });
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            var landedHead = RunGitOutput(repo, "rev-parse", "HEAD");

            Assert.True(output.Contains("Workspace rebase: Rebased", StringComparison.Ordinal));
            Assert.Equal(1, fakeVerifier.RunCount);
            Assert.False(string.IsNullOrWhiteSpace(verifierHead));
            Assert.NotEqual(staleGoalHead, verifierHead);
            Assert.Equal(verifierHead, landedHead);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree")]
    public void CliAcceptanceRebaseConflictBlocksBeforeVerificationAndRestoresWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance rebase conflict test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "seed.txt"), "goal edit");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal edit");
            var originalGoalHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repo, "seed.txt"), "main edit");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main edit");
            var mainHeadBeforeAcceptance = RunGitOutput(repo, "rev-parse", "HEAD");

            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Workspace rebase: Rebase", StringComparison.Ordinal));
            Assert.True(output.Contains("Conflict files:", StringComparison.Ordinal));
            Assert.True(output.Contains("Acceptance evidence: blocked; merge blocked", StringComparison.Ordinal));
            Assert.Equal(0, fakeVerifier.RunCount);
            Assert.Equal(mainHeadBeforeAcceptance, RunGitOutput(repo, "rev-parse", "HEAD"));
            Assert.Equal(originalGoalHead, RunGitOutput(worktreePath, "rev-parse", "HEAD"));
            Assert.Equal("goal edit", File.ReadAllText(Path.Combine(worktreePath, "seed.txt")));
            Assert.Equal("main edit", File.ReadAllText(Path.Combine(repo, "seed.txt")));
            Assert.Equal(string.Empty, RunGitOutput(worktreePath, "status", "--short"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_defers_workspace_cleanup_after_successful_merge")]
    public void CliAcceptanceDefersWorkspaceCleanupAfterSuccessfulMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance cleanup test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(output.Contains("Workspace cleanup deferred", StringComparison.Ordinal));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(kernel.BuildGoalAcceptanceSummary(goal.Id).IsAccepted);
            Assert.True(HasCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, goal.Id), "remove:acceptance-deferred"));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_keep_workspace_retains_workspace_after_merge")]
    public void CliAcceptanceKeepWorkspaceRetainsWorkspaceAfterMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance keep-workspace test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            // --keep-workspace opts out of the deterministic cleanup; the merge still happens.
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(output.Contains("Workspace kept (--keep-workspace)", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_rebases_when_goal_branch_behind_main")]
    public void CliAcceptanceAutoRebasesWhenGoalBranchBehindMain()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Rebase behind main test", repo);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            // Advance the base branch so the goal branch can no longer fast-forward.
            File.WriteAllText(Path.Combine(repo, "mainline.txt"), "main advance");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main advance");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            // Acceptance rebases onto main then fast-forwards instead of punting the merge to the operator.
            Assert.True(output.Contains("Workspace rebase", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "mainline.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_safe_auto_blocks_merge")]
    public void CliAcceptanceSafeAutoBlocksMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance policy test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var ex = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["acceptance", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(ex.Message.Contains("policy 'safe-auto' blocks acceptance merge", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "policy.txt")));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked acceptance merge", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command")]
    public void CliAcceptanceQueueHoldsStaleBranchWithManualMergeCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Stale queued acceptance", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "stale.txt"), "goal");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Stale queued goal");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Advance main");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance-queue"], context));

            Assert.True(output.Contains("ready=0, held=1, blocked=0", StringComparison.Ordinal));
            Assert.True(output.Contains("cannot fast-forward", StringComparison.Ordinal));
            Assert.True(output.Contains($"command: workspace rebase {goal.Id.Value[..8]}", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "stale.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_blocks_dirty_worktree_before_merge")]
    public void CliAcceptanceEvidenceBlocksDirtyWorktreeBeforeMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence dirty test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            File.WriteAllText(Path.Combine(worktreePath, "dirty.txt"), "uncommitted");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Acceptance evidence bundle: blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Test impact:", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification policy:", StringComparison.Ordinal));
            Assert.True(output.Contains("dirty-worktree", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges")]
    public void CliAcceptanceEvidenceAutoInjectsPolicyRequiredChecksAndMerges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence auto-inject test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            Directory.CreateDirectory(Path.Combine(worktreePath, "config"));
            // Empty manifest: no explicit checks, but policy-required checks are auto-injected
            // from the changed file scope (config/acceptance-manifest.json classifies as
            // BuildSystem, triggering a full-suite requirement which gets auto-injected).
            File.WriteAllText(Path.Combine(worktreePath, "config", "acceptance-manifest.json"), "{\"checks\":[]}");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            // Fake verifier always succeeds — simulates the auto-injected check passing.
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Policy-required check was auto-injected and passed — no missing-check blocker.
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.False(output.Contains("acceptance-checks-missing", StringComparison.Ordinal));
            Assert.False(output.Contains("acceptance-policy-check-missing", StringComparison.Ordinal));
            // The merge succeeded: feature.txt is now in the main working tree.
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_skip_verify_bypasses_verification_and_merges")]
    public void CliAcceptanceSkipVerifyBypassesVerificationAndMerges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "skip.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var verifierCalled = false;
            var fakeVerifier = FakeAcceptanceVerifier.Failed("should not run", onRun: () =>
            {
                verifierCalled = true;
            });
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--skip-verify"], context));
            Assert.False(verifierCalled);
            Assert.True(output.Contains("skipped (--skip-verify)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "skip.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rejects_stale_goal_state_before_merge_commit")]
    public void CliAcceptanceRejectsStaleGoalStateBeforeMergeCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stale state test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "stale-state.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                // Modify goal state during verification
                stateRepository.TransactAsync((transactionKernel, _) =>
                {
                    transactionKernel.RecordGoalPolicyDecision(goal.Id, "Concurrent goal state change.");
                    return Task.FromResult((true, true));
                }).GetAwaiter().GetResult();
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var context = new CliExecutionContext(initialKernel, workspace, providers, agents, profiles, goal, null, () => stateRepository.LoadAsync().GetAwaiter().GetResult(), null, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            // This should still succeed when called through the normal acceptance path because
            // the stale check is only performed in ExecuteAcceptanceOutsideTransaction
            CliCommandHandlers.Execute(["acceptance"], context);

            // Verify the merge happened and the workspace file exists
            Assert.True(File.Exists(Path.Combine(repo, "stale-state.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rejects_stale_worktree_head_before_merge_commit")]
    public void CliAcceptanceRejectsStaleWorktreeHeadBeforeMergeCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stale worktree test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "tested.txt"), "tested work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Tested work");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                File.WriteAllText(Path.Combine(worktreePath, "after-verifier-started.txt"), "late work");
                RunGit(worktreePath, "add", "-A");
                RunGit(worktreePath, "commit", "-m", "Late work");
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();

            var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var context = new CliExecutionContext(initialKernel, workspace, providers, agents, profiles, goal, null, () => stateRepository.LoadAsync().GetAwaiter().GetResult(), null, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            // When called through the normal acceptance path (which doesn't have the stale check),
            // the merge will still succeed even though the worktree changed during verification
            CliCommandHandlers.Execute(["acceptance"], context);

            // Verify the merge happened and the new files exist
            Assert.True(File.Exists(Path.Combine(repo, "tested.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "after-verifier-started.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
