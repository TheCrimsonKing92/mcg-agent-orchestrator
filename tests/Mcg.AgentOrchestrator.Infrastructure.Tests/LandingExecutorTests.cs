using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class LandingExecutorTests
{
    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_excludes_auto_recovered_empty_output_flake")]
    public void LandingExecutorFailedCountExcludesAutoRecoveredEmptyOutputFlake()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FlakeThenPass(kernel, goal, task);

        Assert.Equal(0, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_includes_recovered_real_failure")]
    public void LandingExecutorFailedCountIncludesRecoveredRealFailure()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FailThenPass(kernel, goal, task);

        Assert.Equal(1, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_escalates_when_two_genuine_failed_tasks_recovered")]
    public void LandingDecisionEscalatesWhenTwoGenuineFailedTasksRecovered()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester);

        foreach (var task in goal.Tasks)
        {
            FailThenPass(kernel, goal, task);
        }

        var failedCount = LandingExecutor.CountFailedVerifications(goal);
        var decision = LandingDecisionEngine.Decide(new LandingInputs(
            RepositoryChangeClassifier.Classify(["README.md"]),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: failedCount));

        Assert.Equal(LandingDecisionEngine.RepeatedFailureThreshold, failedCount);
        if (decision is not LandingDecision.Escalate escalation)
        {
            throw new InvalidOperationException("Expected landing escalation.");
        }

        Assert.True(escalation.Reason.Contains("repeated failures", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_applies_landed_backlog_add_proposal_once")]
    public void LandingExecutorAppliesLandedBacklogAddProposalOnce()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "checkout", "-b", goalBranch);
            Directory.CreateDirectory(Path.Combine(repo, ".orchestrator-proposals"));
            File.WriteAllText(Path.Combine(repo, ".orchestrator-proposals", "backlog-add-proposed-follow-up.md"), """
                ---
                kind: backlog-add
                title: Proposed follow-up
                ---
                Body from a landed proposal.
                """);
            RunGit(repo, "add", ".orchestrator-proposals/backlog-add-proposed-follow-up.md");
            RunGit(repo, "commit", "-m", "Add state-effect proposal");
            RunGit(repo, "checkout", "main");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            var items = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            var item = Assert.Single(items);
            Assert.Equal("proposed-follow-up", item.Id);
            Assert.Equal("Proposed follow-up", item.Title);
            Assert.Equal(goal.Id.Value, item.SourceGoalId);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation.StartsWith("conductor:state-effect:", StringComparison.Ordinal) &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.Contains(kernel.GetTimeline(goal.Id), evt =>
                evt.Message.Contains("State-effect proposal applied", StringComparison.Ordinal));

            var reapplied = StateEffectProposalApplier.ApplyLandedProposals(
                kernel,
                goal,
                workspace,
                [".orchestrator-proposals/backlog-add-proposed-follow-up.md"]);

            Assert.Single(reapplied);
            Assert.False(reapplied[0].Applied);
            var afterReapply = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            Assert.Single(afterReapply);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Goal_mark_landed_persists_landed_state_before_cleanup_needed_enqueue")]
    public void GoalMarkLandedPersistsLandedStateBeforeCleanupNeededEnqueue()
    {
        var repo = CreateGitRepository();
        var previousWarningSink = GoalWorktrees.CleanupWarningSink;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var innerRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var stateRepository = new CountingStateRepository(innerRepository);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            innerRepository.SaveAsync(kernel).GetAwaiter().GetResult();
            stateRepository.ResetSaveCount();

            var saveCountAtCleanupNeeded = 0;
            GoalWorktrees.CleanupWarningSink = warning =>
            {
                if (warning.Operation.Equals("remove:cleanup-needed", StringComparison.OrdinalIgnoreCase))
                {
                    saveCountAtCleanupNeeded = stateRepository.SaveCount;
                }
            };

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Assert.True(changed);
            Assert.True(saveCountAtCleanupNeeded > 0, "cleanup-needed was enqueued before a landed-state save");
            var persisted = innerRepository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Completed, persisted.Status);
        }
        finally
        {
            GoalWorktrees.CleanupWarningSink = previousWarningSink;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_cleanup_failure_reports_blocker_without_failing_landed_goal")]
    public void TerminalSweepCleanupFailureReportsBlockerWithoutFailingLandedGoal()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = CreateCompletedGoalWithLeftoverWorkspace(repo);
            GoalWorktrees.RecordGoalCleanupNeeded(repo, goal.Id, "remove:simulated-cleanup-failure");

            var result = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            var goalResult = Assert.Single(result.Goals);
            var blocker = Assert.Single(goalResult.Blockers);
            Assert.Equal("completed-worktree-cleanup-needed", blocker.Kind);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.DoesNotContain(
                kernel.GetGoal(goal.Id).Timeline,
                evt => evt.Message.Contains("AcceptanceFailed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_cleanup_second_run_is_noop_after_success")]
    public void TerminalSweepCleanupSecondRunIsNoopAfterSuccess()
    {
        var repo = CreateGitRepository();
        try
        {
            var (kernel, goal) = CreateCompletedGoalWithLeftoverWorkspace(repo);

            var first = TerminalGoalSweep.Run(kernel, repo, goal.Id);
            var second = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.Contains(first.Goals, item => item.Repairs.Any(repair => repair.Kind == "merged-branch-cleanup"));
            Assert.Empty(second.Goals);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_enabled_pushes_main_goal_branch_and_tags_to_bare_remote")]
    public void RemoteMirrorEnabledPushesMainGoalBranchAndTagsToBareRemote()
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository();
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", ToGitFileUrl(remote));
            WriteMirrorConfig(repo, "mirror");
            RunGit(repo, "tag", "mirror-test-tag");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "mirrored.txt", "mirrored");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            var stateEntry = RemoteGitMirror.ReadState(repo).Entries.Single();
            Assert.True(stateEntry.Status == RemoteMirrorEntryStatus.Succeeded, stateEntry.LastError);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, "refs/heads/main");
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
            AssertGitRef(remote, "refs/tags/mirror-test-tag");
            Assert.Contains(GoalOperationJournal.Read(repo, goal.Id).LatestByOperation, entry =>
                entry.Operation == "conductor:mirror:mirror" &&
                entry.Status == GoalOperationStatus.Completed &&
                entry.Detail?.Contains("MirrorSucceeded", StringComparison.Ordinal) == true);
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Theory(DisplayName = "Remote_mirror_disabled_or_unconfigured_does_not_push")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RemoteMirrorDisabledOrUnconfiguredDoesNotPush(bool writeDisabledConfig)
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository();
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", ToGitFileUrl(remote));
            if (writeDisabledConfig)
            {
                WriteMirrorConfig(repo, false, "mirror");
            }

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "no-mirror.txt", "no mirror");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Empty(mirror.Outcomes);
            AssertGitMissingRef(remote, "refs/heads/main");
            Assert.False(File.Exists(RemoteGitMirror.StatePath(repo)));
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Terminal_sweep_does_not_run_remote_mirror_push_inline")]
    public void TerminalSweepDoesNotRunRemoteMirrorPushInline()
    {
        var repo = CreateGitRepository();
        var remote = CreateBareRepository();
        using var hooks = WithMirrorTestHooks();
        var pushCount = 0;
        var oldGitRunner = RemoteGitMirror.GitRunner;
        RemoteGitMirror.GitRunner = (workingDirectory, args) =>
        {
            if (args.Count > 0 && args[0] == "push")
            {
                Interlocked.Increment(ref pushCount);
            }

            return oldGitRunner(workingDirectory, args);
        };

        try
        {
            RunGit(repo, "remote", "add", "mirror", ToGitFileUrl(remote));
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "not-inline.txt", "not inline");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Equal(0, pushCount);
            Assert.Equal(RemoteMirrorEntryStatus.Pending, RemoteGitMirror.ReadState(repo).Entries.Single().Status);
            AssertGitMissingRef(remote, "refs/heads/main");
        }
        finally
        {
            RemoteGitMirror.GitRunner = oldGitRunner;
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_unreachable_remote_defers_and_later_retry_succeeds")]
    public void RemoteMirrorUnreachableRemoteDefersAndLaterRetrySucceeds()
    {
        var repo = CreateGitRepository();
        var remote = Path.Combine(Path.GetTempPath(), "mcg-mirror-tests", Guid.NewGuid().ToString("N"));
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", ToGitFileUrl(remote));
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "retry-mirror.txt", "retry");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var first = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Contains(first.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed);
            var failed = GoalOperationJournal.Read(repo, goal.Id).LatestByOperation.Single(entry =>
                entry.Operation == "conductor:mirror:mirror");
            Assert.Equal(GoalOperationStatus.Failed, failed.Status);
            Assert.Contains("classification=TRANSIENT", failed.Detail, StringComparison.Ordinal);

            Directory.CreateDirectory(remote);
            RunGit(remote, "init", "--bare");
            var second = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.Contains(second.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, "refs/heads/main");
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_retry_uses_landed_branch_tip_after_cleanup_deletes_goal_branch")]
    public void RemoteMirrorRetryUsesLandedBranchTipAfterCleanupDeletesGoalBranch()
    {
        var repo = CreateGitRepository();
        var remote = Path.Combine(Path.GetTempPath(), "mcg-mirror-tests", Guid.NewGuid().ToString("N"));
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "mirror", ToGitFileUrl(remote));
            WriteMirrorConfig(repo, "mirror");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "retry-after-cleanup.txt", "retry after cleanup");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var first = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);
            GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
            kernel.CompleteGoal(goal.Id, "Completed after durable landing.");
            var cleanup = TerminalGoalSweep.Run(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(first.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed);
            Assert.Contains(cleanup.Goals, item => item.Repairs.Any(repair => repair.Kind == "merged-branch-cleanup"));
            Assert.False(GitCli.Run(repo, "show-ref", "--verify", $"refs/heads/{goalBranch}").Succeeded);

            Directory.CreateDirectory(remote);
            RunGit(remote, "init", "--bare");
            var second = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.Contains(second.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded);
            AssertGitRef(remote, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(remote);
        }
    }

    [Xunit.Fact(DisplayName = "Remote_mirror_two_remotes_pushes_reachable_and_defers_unreachable")]
    public void RemoteMirrorTwoRemotesPushesReachableAndDefersUnreachable()
    {
        var repo = CreateGitRepository();
        var reachable = CreateBareRepository();
        var missing = Path.Combine(Path.GetTempPath(), "mcg-mirror-tests", Guid.NewGuid().ToString("N"));
        using var _ = WithMirrorTestHooks();
        try
        {
            RunGit(repo, "remote", "add", "reachable", ToGitFileUrl(reachable));
            RunGit(repo, "remote", "add", "missing", ToGitFileUrl(missing));
            WriteMirrorConfig(repo, "reachable", "missing");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "multi-mirror.txt", "multi");

            var landing = LandingExecutor.Execute(kernel, goal, workspace);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            Assert.NotEmpty(RemoteGitMirror.ReadState(repo).Entries);
            var mirror = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.True(landing.MainAdvanced);
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorSucceeded && outcome.Remote == "reachable");
            Assert.Contains(mirror.Outcomes, outcome => outcome.Kind == RemoteMirrorOutcomeKind.MirrorFailed && outcome.Remote == "missing");
            AssertGitRef(reachable, "refs/heads/main");
            AssertGitRef(reachable, $"refs/heads/{goalBranch}");
        }
        finally
        {
            TryDeleteDirectory(repo);
            TryDeleteDirectory(reachable);
            TryDeleteDirectory(missing);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(params AgentRole[] roles)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = roles
            .Select(role => new TaskSpec(TaskId.New(), $"Run {role} task.", role))
            .ToArray();
        var goal = kernel.CreateGoal("Landing count test", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static void FlakeThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "silent-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "silent-agent",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Auto-retry transient empty-output dispatch flake.");
        Pass(kernel, goal, task);
    }

    private static void FailThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "failing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "failing-agent",
            "C:\\repo",
            1,
            "attempted work",
            "test failed",
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Fix real failure.");
        Pass(kernel, goal, task);
    }

    private static void Pass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "passing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "passing-agent",
            "C:\\repo",
            0,
            "WORKER_RESULT: tests pass",
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string command)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker",
            command,
            "C:\\repo",
            DateTimeOffset.UtcNow));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateVerifiedGoal(string repo)
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return (kernel, goal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateCompletedGoalWithLeftoverWorkspace(string repo)
    {
        var (kernel, goal) = CreateVerifiedGoal(repo);
        var branch = GoalWorktrees.BranchName(goal.Id);
        RunGit(repo, "checkout", "-b", branch);
        File.WriteAllText(Path.Combine(repo, "landed-work.txt"), "landed");
        RunGit(repo, "add", "landed-work.txt");
        RunGit(repo, "commit", "-m", "Goal work");
        RunGit(repo, "checkout", "main");
        RunGit(repo, "merge", "--ff-only", branch);
        RunGit(repo, "worktree", "add", GoalWorktrees.WorktreePath(repo, goal.Id), branch);
        GoalOperationJournal.Completed(repo, goal, "conductor:land", "landed");
        kernel.CompleteGoal(goal.Id, "Already landed; cleanup remains.");
        return (kernel, goal);
    }

    internal static string CreateGitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-landing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DotnetBuildEnvironmentManager.RegisterCurrentLandingTestFixtureRoot(root);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial" + Environment.NewLine);
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "Initial");
        return root;
    }

    private static string CreateBareRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-mirror-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--bare");
        return root;
    }

    private static void AddGoalBranchCommit(string repo, string goalBranch, string fileName, string content)
    {
        RunGit(repo, "checkout", "-b", goalBranch);
        File.WriteAllText(Path.Combine(repo, fileName), content + Environment.NewLine);
        RunGit(repo, "add", fileName);
        RunGit(repo, "commit", "-m", $"Add {fileName}");
        RunGit(repo, "checkout", "main");
    }

    private static void WriteMirrorConfig(string repo, params string[] remotes) =>
        WriteMirrorConfig(repo, enabled: true, remotes);

    private static void WriteMirrorConfig(string repo, bool enabled, params string[] remotes)
    {
        var configDir = Path.Combine(repo, "config");
        Directory.CreateDirectory(configDir);
        var remoteList = string.Join(", ", remotes.Select(remote => $"\"{remote}\""));
        File.WriteAllText(Path.Combine(configDir, "mirror.json"), $$"""
            {
              "enabled": {{enabled.ToString().ToLowerInvariant()}},
              "remotes": [{{remoteList}}],
              "push": {
                "main": true,
                "goalBranch": true,
                "tags": true
              }
            }
            """);
    }

    private static void AssertGitRef(string repository, string reference)
    {
        var result = GitCli.Run(repository, "show-ref", "--verify", reference);
        Assert.Equal(0, result.ExitCode);
    }

    private static void AssertGitMissingRef(string repository, string reference)
    {
        var result = GitCli.Run(repository, "show-ref", "--verify", reference);
        Assert.NotEqual(0, result.ExitCode);
    }

    private static string ToGitFileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static IDisposable WithMirrorTestHooks()
    {
        var oldBackoff = RemoteGitMirror.BackoffForAttempt;
        var oldGitRunner = RemoteGitMirror.GitRunner;
        RemoteGitMirror.BackoffForAttempt = _ => TimeSpan.Zero;
        RemoteGitMirror.GitRunner = TestMirrorGitRunner;
        return new DelegateDisposable(() =>
        {
            RemoteGitMirror.BackoffForAttempt = oldBackoff;
            RemoteGitMirror.GitRunner = oldGitRunner;
        });
    }

    private static GitCli.GitResult TestMirrorGitRunner(string workingDirectory, IReadOnlyList<string> args)
    {
        if (args.Count < 3 || args[0] != "push")
        {
            return GitCli.Run(workingDirectory, args.ToArray());
        }

        var remoteResult = GitCli.Run(workingDirectory, "remote", "get-url", args[1]);
        if (!remoteResult.Succeeded)
        {
            return remoteResult;
        }

        var remotePath = FromGitFileUrl(remoteResult.Output.Trim());
        if (!Directory.Exists(remotePath))
        {
            return new GitCli.GitResult(128, string.Empty, $"fatal: '{remotePath}' does not appear to be a git repository");
        }

        if (args[2] == "--tags")
        {
            return PushTagsForTest(workingDirectory, remotePath);
        }

        return PushRefForTest(workingDirectory, remotePath, args[2]);
    }

    private static GitCli.GitResult PushRefForTest(string workingDirectory, string remotePath, string refspec)
    {
        var refspecParts = refspec.Split(':', 2);
        var sourceRef = refspecParts[0];
        var targetRef = refspecParts.Length == 2 && !string.IsNullOrWhiteSpace(refspecParts[1])
            ? refspecParts[1]
            : refspec;
        var source = GitCli.Run(workingDirectory, "rev-parse", sourceRef);
        if (!source.Succeeded)
        {
            return source;
        }

        targetRef = targetRef.StartsWith("refs/", StringComparison.Ordinal)
            ? targetRef
            : $"refs/heads/{targetRef}";
        CopyGitObjectsForTest(workingDirectory, remotePath);
        return GitCli.Run(remotePath, "update-ref", targetRef, source.Output.Trim());
    }

    private static GitCli.GitResult PushTagsForTest(string workingDirectory, string remotePath)
    {
        var tags = GitCli.Run(workingDirectory, "for-each-ref", "--format=%(refname)", "refs/tags");
        if (!tags.Succeeded || string.IsNullOrWhiteSpace(tags.Output))
        {
            return tags.Succeeded ? new GitCli.GitResult(0, string.Empty, string.Empty) : tags;
        }

        foreach (var line in tags.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var source = GitCli.Run(workingDirectory, "rev-parse", line);
            if (!source.Succeeded)
            {
                return source;
            }

            CopyGitObjectsForTest(workingDirectory, remotePath);
            var update = GitCli.Run(remotePath, "update-ref", line, source.Output.Trim());
            if (!update.Succeeded)
            {
                return update;
            }
        }

        return new GitCli.GitResult(0, string.Empty, string.Empty);
    }

    private static string FromGitFileUrl(string value) =>
        value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(value).LocalPath
            : value;

    private static void CopyGitObjectsForTest(string workingDirectory, string remotePath)
    {
        var gitDirResult = GitCli.Run(workingDirectory, "rev-parse", "--git-dir");
        if (!gitDirResult.Succeeded)
        {
            return;
        }

        var gitDir = gitDirResult.Output.Trim();
        if (!Path.IsPathRooted(gitDir))
        {
            gitDir = Path.GetFullPath(Path.Combine(workingDirectory, gitDir));
        }

        var sourceObjects = Path.Combine(gitDir, "objects");
        var targetObjects = Path.Combine(remotePath, "objects");
        if (!Directory.Exists(sourceObjects) || !Directory.Exists(targetObjects))
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceObjects, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceObjects, sourceFile);
            var targetFile = Path.Combine(targetObjects, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (!File.Exists(targetFile))
            {
                File.Copy(sourceFile, targetFile);
            }
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class CountingStateRepository(ITransactionalOrchestratorStateRepository inner)
        : ITransactionalOrchestratorStateRepository
    {
        private int _saveCount;

        public int SaveCount => Volatile.Read(ref _saveCount);

        public void ResetSaveCount() => Volatile.Write(ref _saveCount, 0);

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public async Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            await inner.SaveAsync(kernel, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _saveCount);
        }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalAsync(goalId, transaction, cancellationToken);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
