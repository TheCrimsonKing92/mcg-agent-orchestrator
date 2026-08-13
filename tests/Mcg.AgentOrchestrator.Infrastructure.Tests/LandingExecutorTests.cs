using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
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

    [Xunit.Fact(DisplayName = "LandingExecutor rereads circuit at integration merge mutation boundary")]
    public void LandingExecutorBlocksWhenCircuitOpensAfterAdmission()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "checkout", "-b", goalBranch);
            File.WriteAllText(Path.Combine(repo, "must-not-land.txt"), "blocked");
            RunGit(repo, "add", "must-not-land.txt");
            RunGit(repo, "commit", "-m", "Candidate goal work");
            RunGit(repo, "checkout", "main");
            var mainBefore = GoalAcceptanceVerifier.ResolveGitText(repo, "rev-parse", "HEAD")!.Trim();
            var checks = 0;

            var result = LandingExecutor.Execute(
                kernel,
                goal,
                workspace,
                mutationBlocker: () =>
                    Interlocked.Increment(ref checks) == 1
                        ? null
                        : "acceptance circuit became Pending");

            Assert.False(result.MainAdvanced);
            Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("held before merge", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, checks);
            Assert.Equal(mainBefore, GoalAcceptanceVerifier.ResolveGitText(repo, "rev-parse", "HEAD")!.Trim());
            Assert.Equal(
                mainBefore,
                GoalAcceptanceVerifier.ResolveGitText(
                    repo,
                    "rev-parse",
                    LandingExecutor.IntegrationBranchName)!.Trim());
            Assert.False(File.Exists(Path.Combine(repo, "must-not-land.txt")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "CLI land result carries authoritative changed paths without a goal worktree")]
    public void LandingResultCarriesChangedPathsFromBranchWithoutWorktree()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            const string enginePath =
                "tests/canary-fixture/branch-only.txt";
            AddGoalBranchCommit(repo, goalBranch, enginePath, "namespace BranchOnly;");
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.Equal([enginePath], result.ChangedFiles);
            Assert.True(File.Exists(Path.Combine(repo, enginePath)));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_non_approval_diff_auto_promotes_without_ownership_hold")]
    public void LandingExecutorGreenGateNonApprovalDiffAutoPromotesWithoutOwnershipHold()
    {
        foreach (var policy in new ConductorAutonomyPolicy?[]
        {
            null,
            ConductorAutonomyPolicy.Conservative,
            ConductorAutonomyPolicy.Manual,
            ConductorAutonomyPolicy.Permissive
        })
        {
            var repo = CreateGitRepository();
            try
            {
                var policyName = policy?.Name ?? "null";
                var workspace = OrchestratorWorkspace.ForDirectory(repo);
                var (kernel, goal) = CreateVerifiedGoal(repo);
                var goalBranch = GoalWorktrees.BranchName(goal.Id);
                AddGoalBranchCommit(repo, goalBranch, "src/Mcg.AgentOrchestrator.App/Feature.cs", "namespace TestApp; internal sealed class Feature;");

                var result = LandingExecutor.Execute(kernel, goal, workspace, policy: policy);

                Assert.True(result.MainAdvanced, policyName);
                var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
                Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            }
            finally
            {
                TryDeleteDirectory(repo);
            }
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_intent_write_failure_prevents_main_merge")]
    public void LandingExecutorIntentWriteFailurePreventsMainMerge()
    {
        var repo = CreateGitRepository();
        var previousHook = GoalOperationJournal.BeforeLandingIntentAppend;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/intent-write-fails.txt", "goal work");
            GoalOperationJournal.BeforeLandingIntentAppend = _ => throw new InvalidOperationException("simulated intent write failure");

            var ex = Assert.Throws<InvalidOperationException>(() => LandingExecutor.Execute(kernel, goal, workspace));

            Assert.Contains("simulated intent write failure", ex.Message);
            Assert.False(IsBranchReachableFromMain(repo, goalBranch));
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            GoalOperationJournal.BeforeLandingIntentAppend = previousHook;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_main_merge_failure_tombstones_landing_intent")]
    public void LandingExecutorMainMergeFailureTombstonesLandingIntent()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/main-merge-fails.txt", "goal work");
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args.SequenceEqual(["merge", "--ff-only", LandingExecutor.IntegrationBranchName])
                    ? new GitCli.GitResult(1, string.Empty, "simulated main merge failure")
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Contains("simulated main merge failure", result.Message);
            Assert.False(IsBranchReachableFromMain(repo, goalBranch));
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.False(GoalOperationJournal.HasDurableLandingIntent(journal));
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Failed);
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_success_records_one_landing_intent_and_reaches_main")]
    public void LandingExecutorSuccessRecordsOneLandingIntentAndReachesMain()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/intent-success.txt", "goal work");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.True(IsBranchReachableFromMain(repo, goalBranch));
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.True(GoalOperationJournal.HasDurableLandingIntent(journal));
            var intent = Assert.Single(journal.Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.LandingIntentOperation &&
                entry.Status == GoalOperationStatus.Completed));
            Assert.Contains("LandingExecutor", intent.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_permissive_auto_promotes_without_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffPermissiveAutoPromotesWithoutOwnershipHold()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal, _, _, _) = CreateVerifiedOwnershipApprovalGoal(repo);

            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: ConductorAutonomyPolicy.Permissive);

            Assert.True(result.MainAdvanced);
            Assert.IsType<LandingDecision.Promote>(result.Decision);
            Assert.True(IsBranchReachableFromMain(repo, GoalWorktrees.BranchName(goal.Id)));
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.OwnershipHold);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_conservative_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffConservativeRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(ConductorAutonomyPolicy.Conservative);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_manual_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffManualRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(ConductorAutonomyPolicy.Manual);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_green_gate_approval_diff_null_policy_records_ownership_hold")]
    public void LandingExecutorGreenGateApprovalDiffNullPolicyRecordsOwnershipHold()
    {
        AssertOwnershipHoldEscalates(null);
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_git_diff_drain_timeout_fails_closed_even_with_empty_stdout")]
    public void LandingExecutorGitDiffDrainTimeoutFailsClosedEvenWithEmptyStdout()
    {
        var repo = CreateGitRepository();
        var previousGitRunner = LandingExecutor.GitRunner;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            AddGoalBranchCommit(repo, goalBranch, "src/Mcg.AgentOrchestrator.App/TimeoutTouched.cs", "namespace TestApp; internal sealed class TimeoutTouched;");
            LandingExecutor.GitRunner = (workingDirectory, args) =>
                args.Length == 3 &&
                args[0].Equals("diff", StringComparison.Ordinal) &&
                args[1].Equals("--name-only", StringComparison.Ordinal)
                    ? new GitCli.GitResult(0, string.Empty, string.Empty, DrainTimedOut: true)
                    : GitCli.Run(workingDirectory, args);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Contains("diff scope unknown", escalation.Reason);
            Assert.Contains("drain timed out", escalation.Reason);
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            var item = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.LandingEscalation));
            Assert.Contains("diff scope unknown", item.Message);
        }
        finally
        {
            LandingExecutor.GitRunner = previousGitRunner;
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
            var innerRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
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

    [Xunit.Fact(DisplayName = "Goal_mark_landed_closes_linked_source_backlog_item")]
    public async Task GoalMarkLandedClosesLinkedSourceBacklogItem()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new BacklogStore(workspace.BacklogStorePath);
            var item = await store.AddAsync("Goal mark landed source");
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
            await repository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            var closed = await store.GetByExactIdAsync(item.Id);
            Assert.True(changed);
            Assert.NotNull(closed);
            Assert.Equal(BacklogItemStatus.Done, closed!.Status);
            var note = Assert.Single(closed.Notes);
            Assert.Contains(goal.Id.Value, note.Text);
            Assert.Contains("integrateCommit=", note.Text);
        }
        finally
        {
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

    private static void AssertOwnershipHoldEscalates(ConductorAutonomyPolicy? policy)
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal, developer, approvalPath, nonApprovalPath) = CreateVerifiedOwnershipApprovalGoal(repo);

            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: policy);

            Assert.False(result.MainAdvanced);
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.True(LandingExecutor.IsOwnershipHoldEscalation(escalation.Reason));
            var inbox = OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]);
            var hold = Assert.Single(inbox.Items.Where(item => item.Kind == OperatorInboxKind.OwnershipHold));
            Assert.Equal(developer.Id.Value, hold.TaskId);
            Assert.Contains(approvalPath, hold.Evidence);
            Assert.DoesNotContain(nonApprovalPath, hold.Evidence);
            Assert.DoesNotContain(inbox.Items, item => item.Kind == OperatorInboxKind.LandingEscalation);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, string ApprovalPath, string NonApprovalPath)
        CreateVerifiedOwnershipApprovalGoal(string repo)
    {
        const string approvalPath = "src/Mcg.AgentOrchestrator.Infrastructure/OwnershipTouched.cs";
        const string nonApprovalPath = "src/Mcg.AgentOrchestrator.App/NonApprovalTouched.cs";
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer);
        var developer = goal.Tasks[0];
        var tester = goal.Tasks[1];
        var reviewer = goal.Tasks[2];
        var baseCommit = ReadGit(repo, "rev-parse", "main");
        Dispatch(kernel, goal, developer, "developer");
        Dispatch(kernel, goal, tester, "tester");
        Dispatch(kernel, goal, reviewer, "reviewer");
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, baseCommit);
        var goalBranch = GoalWorktrees.BranchName(goal.Id);
        RunGit(repo, "checkout", "-b", goalBranch);
        AppendCommit(repo, approvalPath, "namespace TestInfra; internal sealed class OwnershipTouched;");
        var developerResultCommit = ReadGit(repo, "rev-parse", "HEAD");
        AppendCommit(repo, nonApprovalPath, "namespace TestApp; internal sealed class NonApprovalTouched;");
        var testerResultCommit = ReadGit(repo, "rev-parse", "HEAD");
        RunGit(repo, "checkout", "main");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, developerResultCommit);
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, developerResultCommit);
        kernel.RecordDispatchResultCommit(goal.Id, tester.Id, testerResultCommit);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, baseCommit);
        kernel.RecordDispatchResultCommit(goal.Id, reviewer.Id, testerResultCommit);
        kernel.RecordTaskVerification(
            goal.Id,
            developer.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(
            goal.Id,
            reviewer.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return (kernel, goal, developer, approvalPath, nonApprovalPath);
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
        File.AppendAllText(
            Path.Combine(root, ".git", "info", "exclude"),
            ".orchestrator-test-remotes/" + Environment.NewLine +
            ".orchestrator/" + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "README.md"), "initial" + Environment.NewLine);
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "Initial");
        _ = CreateMigratedStateRepository(
            OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
        return root;
    }

    private static void AddGoalBranchCommit(string repo, string goalBranch, string fileName, string content)
    {
        RunGit(repo, "checkout", "-b", goalBranch);
        AppendCommit(repo, fileName, content);
        RunGit(repo, "checkout", "main");
    }

    private static void AppendCommit(string repo, string fileName, string content)
    {
        var path = Path.Combine(repo, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + Environment.NewLine);
        RunGit(repo, "add", fileName);
        RunGit(repo, "commit", "-m", $"Add {fileName}");
    }

    private static string ReadGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        }

        return result.Output.Trim();
    }

    private static bool IsBranchReachableFromMain(string repository, string branch) =>
        GitCli.Run(repository, "merge-base", "--is-ancestor", branch, "main").Succeeded;

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

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);

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

        public async Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default)
        {
            await inner.SaveGoalSnapshotsAsync(goals, cancellationToken).ConfigureAwait(false);
            if (goals.Count > 0)
            {
                Interlocked.Increment(ref _saveCount);
            }
        }

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalAsync(goalId, transaction, cancellationToken);

        public async Task<T> TransactGoalStateAsync<T>(
            GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            var saved = false;
            var result = await inner.TransactGoalStateAsync(
                    goalId,
                    async (state, token) =>
                    {
                        var mutation = await transaction(state, token).ConfigureAwait(false);
                        saved = mutation.ShouldSave && mutation.NewState is not null;
                        return mutation;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (saved)
            {
                Interlocked.Increment(ref _saveCount);
            }

            return result;
        }
    }

}
