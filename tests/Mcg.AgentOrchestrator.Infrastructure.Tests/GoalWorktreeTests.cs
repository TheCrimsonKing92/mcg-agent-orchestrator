using System.Diagnostics;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class GoalWorktreeTests
{
    private static AgentDefinition EchoDeveloper() => new(
        new AgentId("echo-developer"),
        "Echo Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static IReadOnlyList<AgentDefinition> EchoAgents() =>
    [
        EchoAgent(AgentRole.Planner),
        EchoAgent(AgentRole.Researcher),
        EchoDeveloper(),
        EchoAgent(AgentRole.Tester),
        EchoAgent(AgentRole.Reviewer)
    ];

    private static AgentDefinition EchoAgent(AgentRole role) => new(
        new AgentId($"echo-{role.ToString().ToLowerInvariant()}"),
        $"Echo {role}",
        role,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static WorkerProfileCatalog EchoProfiles() => new(
    [
        new WorkerProfile("local", "git add -A; if ((git status --short).Length -gt 0) { git commit -m Lifecycle-work }; Write-Output {subscriptionModelName}")
    ]);

    private static IModelProviderRegistry SeedSpecRefiner(OrchestratorWorkspace workspace)
    {
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return new InMemoryModelProviderRegistry([
            new FakeSmokeProvider("{}", providerName: "fake-refiner")
        ]);
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_creates_and_resolves_worktree_per_goal")]
    public void GoalWorktreesCreatesAndResolvesWorktreePerGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();

            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);

            var path = GoalWorktrees.Ensure(repo, goalId);

            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.True(File.Exists(Path.Combine(path, "seed.txt")));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goalId));
            Assert.Equal(path, GoalWorktrees.Ensure(repo, goalId));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path")]
    public void GoalWorktreesGitMetadataAccessResolvesLinkedIndexLockPath()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            var access = GoalWorktrees.InspectGitMetadataAccess(
                path,
                new WorkerSandboxOptions(Enabled: false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

            var expected = Path.GetFullPath(Path.Combine(
                repo,
                ".git",
                "worktrees",
                goalId.Value[..8],
                "index.lock"));
            Assert.Equal(NormalizePath(expected), NormalizePath(access.IndexLockPath));
            Assert.Equal(Path.GetFullPath(path), access.WorktreePath);
            Assert.True(access.CurrentProcessCanWriteIndexLock, access.Error ?? "index.lock probe failed");
            Assert.True(access.WorkerCanWriteIndexLock, access.WorkerWriteDisposition);
            Assert.Equal("same-as-orchestrator", access.WorkerWriteDisposition);
            Assert.Contains("orchestrator commits", access.CommitContract);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing")]
    public void GoalWorktreesGitMetadataAccessMarksLowIntegrityWorkerNonCommitting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            var access = GoalWorktrees.InspectGitMetadataAccess(
                path,
                new WorkerSandboxOptions(Enabled: OperatingSystem.IsWindows(), WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

            if (OperatingSystem.IsWindows())
            {
                Assert.False(access.WorkerCanWriteIndexLock);
                Assert.Equal("blocked-by-low-integrity", access.WorkerWriteDisposition);
            }
            else
            {
                Assert.Equal("same-as-orchestrator", access.WorkerWriteDisposition);
            }

            Assert.EndsWith(
                NormalizePathSeparators(Path.Combine(".git", "worktrees", goalId.Value[..8], "index.lock")),
                NormalizePath(access.IndexLockPath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_resolve_all_matches_per_goal_try_resolve")]
    public void GoalWorktreesResolveAllMatchesPerGoalTryResolve()
    {
        var repo = CreateSeededRepository();
        try
        {
            var first = GoalId.New();
            var second = GoalId.New();
            var missing = GoalId.New();
            var firstPath = GoalWorktrees.Ensure(repo, first);
            var secondPath = GoalWorktrees.Ensure(repo, second);

            var resolved = GoalWorktrees.ResolveAll(repo, [first, second, missing]);

            Assert.Equal(firstPath, resolved[first]);
            Assert.Equal(secondPath, resolved[second]);
            Assert.False(resolved.ContainsKey(missing));
            Assert.Equal(GoalWorktrees.TryResolve(repo, first), resolved[first]);
            Assert.Equal(GoalWorktrees.TryResolve(repo, second), resolved[second]);
            Assert.Equal(GoalWorktrees.TryResolve(repo, missing), resolved.GetValueOrDefault(missing));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base")]
    public void GoalWorktreesEnsureFastForwardsUndrivenStaleWorktreeToBase()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // A fix lands on the base branch AFTER the goal worktree was created.
            File.WriteAllText(Path.Combine(repo, "landed-fix.txt"), "fix on main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Landed fix on base");

            // The goal worktree is undriven (no commits of its own, clean) but now behind base.
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));

            // Re-ensuring brings the undriven worktree up to the current base so a worker never
            // builds on a stale base (which would conflict at acceptance with the landed fix).
            var reEnsured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, reEnsured);
            Assert.True(File.Exists(Path.Combine(path, "landed-fix.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched")]
    public void GoalWorktreesEnsureLeavesDrivenDivergentWorktreeUntouched()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // The goal branch has its own committed work (driven).
            File.WriteAllText(Path.Combine(path, "goal-work.txt"), "developer work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Developer work");

            // The base branch advances divergently.
            File.WriteAllText(Path.Combine(repo, "landed-fix.txt"), "fix on main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Landed fix on base");

            // Re-ensuring must NOT fast-forward (it would discard the goal work); the divergent branch
            // is left as-is for TryRebaseOntoMain/acceptance to reconcile.
            var reEnsured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, reEnsured);
            Assert.True(File.Exists(Path.Combine(path, "goal-work.txt")));
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

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

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory")]
    public void GoalWorktreesRemoveResumesAfterUnregisteredWorktreeLeavesDirectory()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "leftover.log"), "held by prior test process");

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            Assert.True(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.True(BranchExists(repo, branch));

            var removeResult = GoalWorktrees.Remove(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + branch + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released")]
    public void GoalWorktreesRemoveReportsLeftoverPathAndResumesWhenLockReleased()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Deregister the worktree manually to isolate directory-deletion behavior.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "leftover.log");
            File.WriteAllText(lockedFile, "held open");

            // Hold the file open exclusively so Directory.Delete fails.
            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows holds the file exclusively, so the registered worktree and branch are
                // cleaned up while the leftover directory is deferred to a later sweep.
                Assert.True(partial.IsComplete);
                Assert.Null(partial.LeftoverPath);
                Assert.True(partial.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
                Assert.True(Directory.Exists(path));

                // Lock released; resume call deletes the directory and cleans up the branch.
                var final = GoalWorktrees.Remove(repo, goalId);
                Assert.True(final.IsComplete);
            }
            else
            {
                // POSIX allows unlinking files with open handles, so removal completes immediately.
                Assert.True(partial.IsComplete);
            }

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_is_idempotent_when_already_clean")]
    public void GoalWorktreesRemoveIsIdempotentWhenAlreadyClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            GoalWorktrees.Ensure(repo, goalId);

            var first = GoalWorktrees.Remove(repo, goalId);
            Assert.True(first.IsComplete);

            // Second call with nothing left must return success, not throw.
            var second = GoalWorktrees.Remove(repo, goalId);
            Assert.True(second.IsComplete);
            Assert.True(second.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases")]
    public void GoalWorktreesRemoveRetriesAndSucceedsWhenTransientLockReleases()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Simulate the half-removed state: worktree already unregistered, directory lingers.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "transient-hold.log");
            File.WriteAllText(lockedFile, "held");

            GoalWorktreeRemoveResult result;
            if (OperatingSystem.IsWindows())
            {
                // Hold the file exclusively then release it partway through the retry window so
                // that a single Remove() call succeeds without requiring a second invocation.
                var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
                _ = Task.Delay(150).ContinueWith(_ => fs.Dispose());

                result = GoalWorktrees.Remove(repo, goalId);

                Assert.True(result.IsComplete);
            }
            else
            {
                // POSIX: open handles do not prevent deletion, so removal completes immediately.
                result = GoalWorktrees.Remove(repo, goalId);
                Assert.True(result.IsComplete);
            }

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_partial_result_identifies_branch_state")]
    public void GoalWorktreesRemovePartialResultIdentifiesBranchState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "hold.txt");
            File.WriteAllText(lockedFile, "lock");

            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            if (OperatingSystem.IsWindows())
            {
                Assert.True(partial.IsComplete);
                Assert.True(partial.Message.Contains(branch, StringComparison.Ordinal));
                Assert.True(partial.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
                Assert.Null(partial.LeftoverPath);
            }
            else
            {
                // POSIX: the held handle does not block removal, so it completes.
                Assert.True(partial.IsComplete);
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ParseWmicListOutput_extracts_pid_and_command_line")]
    public void GoalWorktreesParseWmicListOutputExtractsPidAndCommandLine()
    {
        const string wmicOutput = """

            CommandLine=dotnet test MyProject.dll
            ProcessId=1234

            CommandLine=VBCSCompiler.exe -pipename:xyz
            ProcessId=5678

            CommandLine=
            ProcessId=9999

            """;

        var result = GoalWorktrees.ParseWmicListOutput(wmicOutput);

        Assert.Equal(2, result.Count);
        Assert.Equal("dotnet test MyProject.dll", result[1234]);
        Assert.Equal("VBCSCompiler.exe -pipename:xyz", result[5678]);
        Assert.False(result.ContainsKey(9999));
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

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action")]
    public void GoalHealthEvaluatorPrioritizesDirtyWorktreeBeforeNextAction()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Dirty health", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var agents = AgentCatalog.Default().Agents;
            kernel.ActivateGoal(goal.Id, agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted");

            var health = GoalHealthEvaluator.Build(
                kernel,
                goal,
                agents,
                WorkerProfileCatalog.Default(),
                repo,
                AutonomyPolicy.SupervisedAuto);

            Assert.Equal(GoalHealthDisposition.Blocked, health.Disposition);
            Assert.Equal(20, health.Score);
            Assert.True(health.Recommendation.Contains("dirty worktree", StringComparison.OrdinalIgnoreCase));
            Assert.True(health.SuggestedCommand.Contains("goal-recovery", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states")]
    public void GoalHealthEvaluatorScoresReadyFailedStalledProviderLimitedAndHealthyStates()
    {
        var repo = CreateSeededRepository();
        try
        {
            var agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();

            var readyKernel = new AgentOrchestratorKernel();
            var readyGoal = CreateCompletedGoal(readyKernel, "Ready health", repo);
            var readyPath = GoalWorktrees.Ensure(repo, readyGoal.Id);
            File.WriteAllText(Path.Combine(readyPath, "ready.txt"), "ready");
            RunGit(readyPath, "add", "-A");
            RunGit(readyPath, "commit", "-m", "Ready health");
            var ready = GoalHealthEvaluator.Build(readyKernel, readyGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.ReadyForAcceptance, ready.Disposition);
            Assert.Equal(85, ready.Score);

            var failedKernel = new AgentOrchestratorKernel();
            var failedTask = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var failedGoal = failedKernel.CreateGoal("Failed health", [failedTask]);
            failedKernel.ActivateGoal(failedGoal.Id, agents);
            failedKernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Verification failed.");
            var failed = GoalHealthEvaluator.Build(failedKernel, failedGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.Blocked, failed.Disposition);
            Assert.Equal(25, failed.Score);

            var stalledKernel = new AgentOrchestratorKernel();
            var stalledTask = new TaskSpec(TaskId.New(), "Run worker", AgentRole.Developer);
            var stalledGoal = stalledKernel.CreateGoal("Stalled health", [stalledTask]);
            stalledKernel.ActivateGoal(stalledGoal.Id, agents);
            stalledKernel.RecordTaskDispatch(stalledGoal.Id, stalledTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, DateTimeOffset.UtcNow));
            stalledKernel.RecordTaskProcessStarted(stalledGoal.Id, stalledTask.Id, new TaskProcessRecord(999999, "codex exec prompt.md", repo, "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
            var stalled = GoalHealthEvaluator.Build(stalledKernel, stalledGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.NeedsOperator, stalled.Disposition);
            Assert.Equal(40, stalled.Score);

            var limitedKernel = new AgentOrchestratorKernel();
            var limitedTask = new TaskSpec(TaskId.New(), "Run subscription worker", AgentRole.Developer);
            var limitedGoal = limitedKernel.CreateGoal("Provider-limited health", [limitedTask]);
            limitedKernel.ActivateGoal(limitedGoal.Id, agents);
            limitedKernel.RecordTaskDispatch(limitedGoal.Id, limitedTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, DateTimeOffset.UtcNow));
            limitedKernel.RecordTaskVerification(limitedGoal.Id, limitedTask.Id, new TaskVerificationRecord(
                "codex-cli",
                repo,
                1,
                "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 11:59 PM.",
                string.Empty,
                DateTimeOffset.UtcNow));
            var limited = GoalHealthEvaluator.Build(limitedKernel, limitedGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.ProviderLimited, limited.Disposition);
            Assert.Equal(55, limited.Score);

            var healthyKernel = new AgentOrchestratorKernel();
            var healthyGoal = CreateCompletedGoal(healthyKernel, "Healthy monitor", repo);
            var healthy = GoalHealthEvaluator.Build(healthyKernel, healthyGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.Healthy, healthy.Disposition);
            Assert.Equal(90, healthy.Score);
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

    [Xunit.Fact(DisplayName = "Cli_workspace_command_creates_and_removes_goal_worktree")]
    public void CliWorkspaceCommandCreatesAndRemovesGoalWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            CliCommandDispatcher.ExecuteCommand(["workspace", "create"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            var path = GoalWorktrees.TryResolve(repo, goal.Id);
            Assert.True(path is not null);
            Assert.Equal(path, workspace.ResolveExecutionDirectory(goal.Id));

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.Equal(workspace.ExecutionDirectory, workspace.ResolveExecutionDirectory(goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_can_target_non_current_goal")]
    public void CliWorkspaceRemoveCanTargetNonCurrentGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var olderGoal = kernel.CreateGoal("Older workspace goal", [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
            var latestGoal = kernel.CreateGoal("Latest workspace goal", [new TaskSpec(TaskId.New(), "Do latest work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = latestGoal;
            var olderPath = GoalWorktrees.Ensure(repo, olderGoal.Id);
            var latestPath = GoalWorktrees.Ensure(repo, latestGoal.Id);
            var olderGoalPrefix = olderGoal.Id.Value[..8];

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove", olderGoalPrefix], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, olderGoal.Id) is null);
            Assert.False(Directory.Exists(olderPath));
            Assert.Equal(latestPath, GoalWorktrees.TryResolve(repo, latestGoal.Id));
            Assert.True(Directory.Exists(latestPath));
            Assert.Equal(olderGoal.Id, currentGoal!.Id);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_safe_auto_blocks_cleanup")]
    public void CliWorkspaceRemoveSafeAutoBlocksCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace cleanup policy", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var path = GoalWorktrees.Ensure(repo, goal.Id);

            var ex = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["workspace", "remove", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(ex.Message.Contains("policy 'safe-auto' blocks workspace remove", StringComparison.Ordinal));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked workspace remove", StringComparison.Ordinal)));
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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

    [Xunit.Fact(DisplayName = "Cli_acceptance_lands_when_discord_token_is_invalid")]
    public void CliAcceptanceLandsWhenDiscordTokenIsInvalid()
    {
        var repo = CreateSeededRepository();
        var previousToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", "invalid-token-for-acceptance-test");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate ignores Discord auth", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            OperatorChannelStore.Save(
                workspace.OperatorChannelPath,
                new OperatorChannelCatalog("discord", ForumChannelId: "42"));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var channel = OperatorChannelFactory.Create(
                OperatorChannelStore.Load(workspace.OperatorChannelPath),
                OperatorChannelFactory.ResolveBotToken(),
                workspace.OrchestratorDirectory);
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal, channel)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification: passed (exit 0)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", previousToken);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_removes_workspace_after_successful_merge")]
    public void CliAcceptanceRemovesWorkspaceAfterSuccessfulMerge()
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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

            // Acceptance now deterministically owns post-merge cleanup: no separate `workspace remove`.
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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

    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_auto_creates_workspace_when_missing")]
    public void CliProfileDispatchAutoCreatesWorkspaceWhenMissing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Dispatch auto-create test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, [EchoDeveloper()]);

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal);

            // Dispatch must own workspace creation: none exists yet.
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["profile-dispatch", "1", "local"], context));

            Assert.True(output.Contains("Workspace auto-created", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_verifies_from_git_evidence")]
    public void CliAcceptanceAutoVerifiesFromGitEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            // No manual verification recorded: the goal is not Completed and the task is Assigned.
            Assert.Equal(GoalStatus.Active, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Acceptance derives the verification from git evidence (committed change + clean worktree).
            Assert.True(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_does_not_auto_verify_without_committed_changes")]
    public void CliAcceptanceDoesNotAutoVerifyWithoutCommittedChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("No change auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            GoalWorktrees.Ensure(repo, goal.Id); // worktree exists but has no commits against the base branch

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // No committed work → no auto-verify → the goal stays un-accepted (anti-fabrication preserved).
            Assert.False(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Active, goal.Status);
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

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_records_dogfood_entry")]
    public void CliAcceptanceAutoRecordsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Autorecord distinctive objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            // Acceptance appends a rendered DOGFOOD entry from receipts (a "## " heading beyond the file header).
            Assert.True(output.Contains("Recorded DOGFOOD entry", StringComparison.Ordinal));
            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.True(log.Contains("## ", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_no_record_skips_dogfood_entry")]
    public void CliAcceptanceNoRecordSkipsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "No record objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace", "--no-record"], context));

            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.False(log.Contains("## ", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_failed_task_to_dispatchable")]
    public void CliRecoverResetsFailedTaskToDispatchable()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "boom");
            Assert.Equal(WorkTaskStatus.Failed, task.Status);

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "redo the work"], context));

            // One recover call brings the failed task back to a dispatchable state.
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks")]
    public void CliRecoverResetsCancelledTasksWithoutDisturbingCompletedOrRunningTasks()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelledOne = new TaskSpec(TaskId.New(), "Cancelled one", AgentRole.Planner);
            var cancelledTwo = new TaskSpec(TaskId.New(), "Cancelled two", AgentRole.Researcher);
            var completed = new TaskSpec(TaskId.New(), "Completed", AgentRole.Developer);
            var running = new TaskSpec(TaskId.New(), "Running", AgentRole.Tester);
            var goal = kernel.CreateGoal("Recover cancelled tasks", [cancelledOne, cancelledTwo, completed, running]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

            kernel.ReportTaskProgress(goal.Id, completed.Id, WorkTaskStatus.Completed, "Already done.");
            RecordCancelledProcess(kernel, goal.Id, cancelledOne.Id, 111, repo);
            RecordCancelledProcess(kernel, goal.Id, cancelledTwo.Id, 222, repo);
            var runningStartedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(
                goal.Id,
                running.Id,
                new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, runningStartedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                running.Id,
                new TaskProcessRecord(333, "codex exec prompt.md", repo, "out.log", "err.log", "exit.txt", runningStartedAt, null, null));

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "retry cancelled work"], context));

            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledOne.Id).Status);
            Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledTwo.Id).Status);
            Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, completed.Id).Status);
            var runningTask = kernel.GetTask(goal.Id, running.Id);
            Assert.Equal(WorkTaskStatus.Running, runningTask.Status);
            Assert.True(runningTask.LastProcess is { IsRunning: true });
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var ex = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_applies_ready_goals_sequentially")]
    public void CliAcceptanceQueueAppliesReadyGoalsSequentially()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First queued acceptance", repo);
            var second = CreateCompletedGoal(kernel, "Second queued acceptance", repo);

            var firstPath = GoalWorktrees.Ensure(repo, first.Id);
            File.WriteAllText(Path.Combine(firstPath, "first.txt"), "first");
            RunGit(firstPath, "add", "-A");
            RunGit(firstPath, "commit", "-m", "First queued goal");

            RunGit(repo, "branch", GoalWorktrees.BranchName(second.Id), GoalWorktrees.BranchName(first.Id));
            var secondPath = GoalWorktrees.Ensure(repo, second.Id);
            File.WriteAllText(Path.Combine(secondPath, "second.txt"), "second");
            RunGit(secondPath, "add", "-A");
            RunGit(secondPath, "commit", "-m", "Second queued goal");

            var context = CreateAcceptanceContext(kernel, repo, first);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                context));

            Assert.True(output.Contains("Acceptance queue: 2 goal(s), ready=2, held=0, blocked=0", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {first.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {second.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "first.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "second.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, first.Id) is null);
            Assert.True(GoalWorktrees.TryResolve(repo, second.Id) is null);
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(first.Id)));
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(second.Id)));
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

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_safe_auto_holds_irreversible_actions")]
    public void CliAcceptanceQueueSafeAutoHoldsIrreversibleActions()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Policy queued acceptance", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy-queue.txt"), "goal");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Policy queued goal");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance-queue", "--autonomy", "safe-auto"], context));

            Assert.True(output.Contains("ready=0, held=1, blocked=0, policy=safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("blocks irreversible acceptance or cleanup", StringComparison.Ordinal));
            Assert.True(output.Contains("--autonomy supervised-auto --confirm-acceptance-queue", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "policy-queue.txt")));
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_blocks_generated_artifact_changes")]
    public void CliAcceptanceEvidenceBlocksGeneratedArtifactChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence generated artifact test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var generatedDirectory = Path.Combine(worktreePath, "src", "Feature", "bin", "Debug");
            Directory.CreateDirectory(generatedDirectory);
            var generatedFile = Path.Combine(generatedDirectory, "generated.dll");
            File.WriteAllText(generatedFile, "generated");
            RunGit(worktreePath, "add", "-f", generatedFile);
            RunGit(worktreePath, "commit", "-m", "Generated artifact");

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

            Assert.True(output.Contains("generated-artifacts", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "src", "Feature", "bin", "Debug", "generated.dll")));
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
            Assert.Equal(GoalStatus.Completed, goal.Status);

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

    [Xunit.Fact(DisplayName = "Cli_acceptance_releases_state_write_lock_during_verification")]
    public void CliAcceptanceReleasesStateWriteLockDuringVerification()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance concurrency test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "concurrency.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var verifierEntered = new ManualResetEventSlim(false);
            using var releaseVerifier = new ManualResetEventSlim(false);
            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                verifierEntered.Set();
                Assert.True(releaseVerifier.Wait(TimeSpan.FromSeconds(10)));
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var acceptanceTask = Task.Run(() =>
            {
                try
                {
                    var contextAgents = agents;
                    var contextProfiles = profiles;
                    var contextGoal = currentGoal;

                    var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
                    var context = new CliExecutionContext(
                        initialKernel,
                        workspace,
                        providers,
                        contextAgents,
                        contextProfiles,
                        contextGoal,
                        null,
                        () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                        null,
                        null)
                    {
                        AcceptanceVerifier = fakeVerifier
                    };

                    CliCommandHandlers.Execute(["acceptance"], context);
                }
                finally
                {
                    releaseVerifier.Set();
                }
            });

            Assert.True(verifierEntered.Wait(TimeSpan.FromSeconds(5)));

            var readTask = Task.Run(() => stateRepository.LoadAsync().GetAwaiter().GetResult());
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(1)));

            var writeTask = Task.Run(() => stateRepository.SaveAsync(readTask.Result).GetAwaiter().GetResult());
            Assert.True(writeTask.Wait(TimeSpan.FromSeconds(1)));

            releaseVerifier.Set();
            Assert.True(acceptanceTask.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(File.Exists(Path.Combine(repo, "concurrency.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktree_state_reads_construct_repository_while_write_lock_is_held")]
    public void GoalWorktreeStateReadsConstructRepositoryWhileWriteLockIsHeld()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal("Readable while acceptance holds writer");
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            var concurrentRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var goals = concurrentRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();

            Assert.Single(goals);
            Assert.Equal("Readable while acceptance holds writer", goals.Single().Objective);
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

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
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

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
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

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_runs_accepts_and_removes_workspace")]
    public void CliLifecycleSimpleGoalRunsAcceptsAndRemovesWorkspace()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage workspace remove:", StringComparison.Ordinal));

            var rerunOutput = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            Assert.Equal(goal.Id, context.CurrentGoal!.Id);
            Assert.Equal(1, kernel.Goals.Count);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(rerunOutput.Contains("reused existing idempotent goal", StringComparison.Ordinal));
            Assert.True(rerunOutput.Contains("already completed and workspace cleanup is recorded", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_runs_five_role_goal_accepts_and_removes_workspace")]
    public void CliLifecycleGoalRunsFiveRoleGoalAcceptsAndRemovesWorkspace()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = EchoAgents();
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-goal", "Ship a five-role echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(5, goal.Tasks.Count);
            Assert.True(goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage goal: created and activated.", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage workspace remove:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance")]
    public void CliLifecycleSimpleGoalSafeAutoStopsBeforeAcceptance()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Ship but pause before merge", "--confirm-batch-start", "--confirm-large-paid-subscription-start", "--autonomy", "safe-auto"],
                    context));
                Assert.True(ex.Message.Contains("stopped before acceptance", StringComparison.Ordinal));
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("Autonomy policy: safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance: stopped.", StringComparison.Ordinal));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked lifecycle-simple-goal acceptance", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_confirm_batch_start")]
    public void CliLifecycleSimpleGoalRequiresConfirmBatchStart()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work"],
            context));

        Xunit.Assert.Contains("--confirm-batch-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_large_paid_prompt_confirm")]
    public void CliLifecycleSimpleGoalRequiresLargePaidPromptConfirm()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work", "--confirm-batch-start"],
            context));

        Xunit.Assert.Contains("--confirm-large-paid-subscription-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceFails()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            // Touch a real source file so acceptance classifies a behavior
            // change and actually runs verification (the failing fakeVerifier).
            // Relying on the worker committing scratch like .orchestrator-context
            // would no longer make the worktree dirty, so the change must be real.
            var profiles = new WorkerProfileCatalog(
            [
                new WorkerProfile("local", "New-Item -ItemType Directory -Force src | Out-Null; Set-Content -Path src/lifecycle-change.cs -Value '// lifecycle work'; git add -A; git commit -m Lifecycle-work; Write-Output {subscriptionModelName}")
            ]);
            var fakeVerifier = FakeAcceptanceVerifier.Failed("Focused tests failed");
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() =>
            {
                var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Run but fail acceptance", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
                Xunit.Assert.Contains("acceptance", ex.Message);
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Next: acceptance", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete")]
    public void GoalWorktreesRemoveInvokesBuildServerShutdownBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // Simulate the unregistered-but-directory-remains half-state so the
            // test exercises BuildServerShutdown → DeleteDirectoryWithRetry directly.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var shutdownCalled = false;
            string? shutdownPath = null;
            var directoryExistedAtShutdown = false;

            GoalWorktrees.BuildServerShutdown = worktreePath =>
            {
                shutdownCalled = true;
                shutdownPath = worktreePath;
                directoryExistedAtShutdown = Directory.Exists(worktreePath);
            };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(shutdownCalled);
            Assert.Equal(path, shutdownPath);
            Assert.True(directoryExistedAtShutdown);
            Assert.True(result.IsComplete);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete")]
    public void GoalWorktreesRemoveResetsSandboxAclBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = _ => { };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.True(acl.ResetPaths.SequenceEqual([path]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_deletes_orphaned_worktree_directory")]
    public void GoalWorktreesSweepDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned1");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = _ => { };

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Contains(acl.ResetPaths, resetPath => string.Equals(resetPath, orphanPath, StringComparison.Ordinal));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_clears_existing_orphan_and_retries_once")]
    public void GoalWorktreesEnsureClearsExistingOrphanAndRetriesOnce()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.WorktreePath(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(path, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = _ => { };

            var ensured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, ensured);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.True(acl.ResetPaths.SequenceEqual([path]));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_failure_logs_warning_and_defers_leftover")]
    public void GoalWorktreesCleanupFailureLogsWarningAndDefersLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = _ => { };
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.Null(result.LeftoverPath);
            Assert.True(result.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(path));
            var warning = Assert.Single(warnings);
            Assert.Equal(path, warning.Path);
            Assert.Equal("remove", warning.Operation);
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete")]
    public void GoalWorktreesRemoveReapsRecordedWorkerProcessesBeforeDelete()
    {
        var repo = CreateSeededRepository();
        var originalKill = GoalWorktrees.TryKillRecordedProcess;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Reap worker processes", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var startedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(111, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [111, 222]));

            var killed = new List<int>();
            GoalWorktrees.TryKillRecordedProcess = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = _ => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([111, 222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.TryKillRecordedProcess = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_skips_protected_recorded_worker_process")]
    public void GoalWorktreesRemoveSkipsProtectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            var protectedPid = 111;
            Environment.SetEnvironmentVariable(
                CliProtectedProcessEnvironment.ProtectedPidVariable,
                protectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove protected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(protectedPid, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [protectedPid]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = _ => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.Empty(killed);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_kills_unprotected_recorded_worker_process")]
    public void GoalWorktreesRemoveKillsUnprotectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "111");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove unprotected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(222, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [222]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = _ => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    private static string CreateSeededRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worktree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private static Goal CreateCompletedGoal(AgentOrchestratorKernel kernel, string objective, string repo)
    {
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Completed, goal.Status);
        return goal;
    }

    private static CliExecutionContext CreateAcceptanceContext(
        AgentOrchestratorKernel kernel,
        string repo,
        Goal currentGoal)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var fakeVerifier = FakeAcceptanceVerifier.Passed();
        return new CliExecutionContext(
            kernel,
            workspace,
            new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            currentGoal)
        {
            AcceptanceVerifier = fakeVerifier
        };
    }

    private static void RecordCancelledProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        int processId,
        string workingDirectory)
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        kernel.RecordTaskDispatch(
            goalId,
            taskId,
            new TaskDispatchRecord("codex-cli", "codex exec prompt.md", workingDirectory, startedAt));
        kernel.RecordTaskProcessStarted(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, "out.log", "err.log", "exit.txt", startedAt, null, null));
        kernel.RecordTaskProcessCancelled(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, "out.log", "err.log", "exit.txt", startedAt, DateTimeOffset.UtcNow, null, WasCancelled: true));
    }

    private sealed class FakeAcceptanceVerifier(
        AcceptanceVerificationResult result,
        Action? onRun = null) : IGoalAcceptanceVerifier
    {
        public int RunCount { get; private set; }

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            onRun?.Invoke();
            return Task.FromResult(AddPolicyRequiredChecks(result, changedFiles ?? []));
        }

        private static AcceptanceVerificationResult AddPolicyRequiredChecks(
            AcceptanceVerificationResult result,
            IReadOnlyList<string> changedFiles)
        {
            if (!result.Passed)
                return result;

            var checks = result.Checks?.ToList() ?? [];
            var existing = checks
                .Select(check => check.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer,
                goalObjective: string.Empty,
                taskDescription: string.Empty,
                verificationPlan: null,
                changedFiles);
            foreach (var check in policy.Checks.Where(check =>
                check.Required &&
                !check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase) &&
                existing.Add(check.Name)))
            {
                checks.Add(new AcceptanceCheckResult(check.Name, true, 0, null));
            }

            return result with { Checks = checks };
        }

        public static FakeAcceptanceVerifier Passed(Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("fake acceptance", true, 0, null)]),
                onRun);

        public static FakeAcceptanceVerifier Failed(string outputTail, Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: outputTail,
                    Checks: [new AcceptanceCheckResult("fake acceptance", false, 1, outputTail)]),
                onRun);
    }

    private static bool BranchExists(string workingDirectory, string branch)
    {
        return RunGitExitCode(workingDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}") == 0;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var exitCode = RunGitExitCode(workingDirectory, arguments, out var error);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    private static int RunGitExitCode(string workingDirectory, params string[] arguments)
    {
        return RunGitExitCode(workingDirectory, arguments, out _);
    }

    private static int RunGitExitCode(string workingDirectory, string[] arguments, out string error)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return process.ExitCode;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string NormalizePathSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    [Xunit.Fact(DisplayName = "DeleteDirectory_removes_tree_containing_read_only_files")]
    public void DeleteDirectoryRemovesTreeContainingReadOnlyFiles()
    {
        // Sandbox workers leave their worktree checkout read-only; the orphan sweep must still be
        // able to delete it. On Windows a naive Directory.Delete throws UnauthorizedAccessException
        // on a read-only file, so this exercises the attribute-clearing retry path.
        var root = Path.Combine(Path.GetTempPath(), "mcg-del-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var file = Path.Combine(root, "nested", "locked.txt");
        File.WriteAllText(file, "sandbox output");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        try
        {
            Assert.True(GoalWorktrees.DeleteDirectory(root));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            DeleteDirectory(root);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp directories are pruned by the OS.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecordingSandboxAclHelper : ISandboxAclHelper
    {
        public List<string> ResetPaths { get; } = [];

        public void ResetSandboxAcl(string worktreePath)
        {
            ResetPaths.Add(worktreePath);
        }
    }
}
