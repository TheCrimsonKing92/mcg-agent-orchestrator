using System.Collections.Concurrent;
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

public sealed class GoalWorktreeTestsRemoveCleanupLifecycleCommands : GoalWorktreeTestBase
{

    [Xunit.Fact(DisplayName = "Cli_goal_recovery_prints_cleanup_backoff_skip_until")]
    public void CliGoalRecoveryPrintsCleanupBackoffSkipUntil()
    {
        var repo = CreateSeededRepository();
        var originalDelete = CleanupHooks.DeleteDirectory;
        var originalAcl = CleanupHooks.SandboxAclHelper;
        var originalShutdown = CleanupHooks.BuildServerShutdown;
        var originalNow = CleanupHooks.CleanupUtcNow;
        var originalBackoff = CleanupHooks.CleanupBackoffDuration;
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
        try
        {
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover cleanup backoff", [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            CleanupHooks.DeleteDirectory = _ => false;
            CleanupHooks.SandboxAclHelper = new RecordingSandboxAclHelper();
            CleanupHooks.BuildServerShutdown = (_, _) => { };
            CleanupHooks.CleanupUtcNow = () => now;
            CleanupHooks.CleanupBackoffDuration = TimeSpan.FromMinutes(10);
            CleanupHooks.FindLockHoldersForCleanup = _ => [];
            _ = RemoveWorktree(repo, goal.Id);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-recovery", goal.Id.Value[..8]],
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                cleanupContext: new WorktreeCleanupContext(CleanupHooks.Build())));

            Assert.Contains("Cleanup backoff:", output);
            Assert.Contains("reason=remove:leftover-directory", output);
            Assert.Contains("skip_until_utc=2026-07-02T05:10:00.0000000+00:00", output);
            Assert.Contains("remaining_wait=00:10:00", output);
            Assert.Contains($"workspace remove {goal.Id.Value[..8]}", output);
        }
        finally
        {
            CleanupHooks.DeleteDirectory = originalDelete;
            CleanupHooks.SandboxAclHelper = originalAcl;
            CleanupHooks.BuildServerShutdown = originalShutdown;
            CleanupHooks.CleanupUtcNow = originalNow;
            CleanupHooks.CleanupBackoffDuration = originalBackoff;
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_repair_clears_stale_failure_only_with_landing_and_cleanup_evidence")]
    public void CliAcceptanceRepairClearsStaleFailureOnlyWithLandingAndCleanupEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var blockedGoal = CreateCompletedGoal(kernel, "Repair blocked goal", repo);
            kernel.RecordAcceptanceFailure(blockedGoal.Id, ["acceptance evidence blocked"]);
            var blockedContext = CreateAcceptanceContext(kernel, repo, blockedGoal);

            var blocked = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-repair", blockedGoal.Id.Value[..8], "--confirm-acceptance-repair"],
                blockedContext));
            Assert.Contains("no completed acceptance or conductor landing evidence", blocked.Message);
            Assert.NotNull(kernel.GetGoal(blockedGoal.Id).LatestAcceptanceFailure);

            var repairGoal = CreateCompletedGoal(kernel, "Repair landed cleaned goal", repo);
            kernel.RecordAcceptanceFailure(repairGoal.Id, ["acceptance evidence blocked"]);
            GoalOperationJournal.Completed(repo, repairGoal, "conductor:land", "landed");
            GoalOperationJournal.Completed(repo, repairGoal, "conductor:cleanup", "cleaned");
            var repairContext = CreateAcceptanceContext(kernel, repo, repairGoal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-repair", repairGoal.Id.Value[..8], "--confirm-acceptance-repair"],
                repairContext));

            Assert.Contains("Acceptance repaired:", output);
            Assert.Null(kernel.GetGoal(repairGoal.Id).LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_cleanup_failure_reports_cleanup_blocker_after_landing_stays_accepted")]
    public void CliAcceptanceCleanupFailureReportsCleanupBlockerAfterLandingStaysAccepted()
    {
        var repo = CreateSeededRepository();
        var originalDeleteDirectory = CleanupHooks.DeleteDirectory;
        try
        {
            var shutdownRequests = CaptureBuildServerShutdownRequests();
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance cleanup leftover test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));

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
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                CleanupContext = CreateIsolatedCleanupContext(repo)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal), output);
            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
            Assert.True(acceptance.IsAccepted);

            // Metadata removal has completed, but the owned directory still needs deletion.
            // This forces the sweep through the injected directory failure rather than git's delete.
            File.Delete(Path.Combine(worktreePath, ".git"));
            RunGit(repo, "worktree", "prune");
            var pending = Assert.IsType<GoalWorktreeCleanupBackoff>(TryGetCleanupBackoff(repo, goal.Id));
            var retryTime = pending.SkipUntilUtc.AddSeconds(1);
            var deleteAttempts = 0;
            CleanupHooks.CleanupUtcNow = () => retryTime;
            CleanupHooks.DeleteDirectory = cleanupPath =>
            {
                if (!cleanupPath.Equals(worktreePath, StringComparison.OrdinalIgnoreCase))
                {
                    return originalDeleteDirectory(cleanupPath);
                }
                deleteAttempts++;
                return false;
            };

            var sweep = TerminalGoalSweep.Run(kernel, repo, goal.Id, cleanupHooks: CleanupHooks.Build());

            Assert.True(deleteAttempts > 0, "The terminal sweep must execute the injected deletion failure.");
            var blocker = Assert.Single(sweep.Blockers);
            Assert.Equal("completed-worktree-cleanup-needed", blocker.Kind);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(kernel.BuildGoalAcceptanceSummary(goal.Id).IsAccepted);
            Assert.True(Directory.Exists(worktreePath));
            AssertBuildServerShutdownRequests(shutdownRequests, worktreePath);
        }
        finally
        {
            CleanupHooks.DeleteDirectory = originalDeleteDirectory;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing")]
    public async Task CliAcceptanceQueueApplyPersistsCleanupAfterOutsideTransactionRouting()
    {
        var repo = CreateReducedAcceptanceCohortRepository(renameInitialBranchToMain: false);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Queued acceptance persistence", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["previous verifier failure"]);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "queue-persist.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Queued persistence goal");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = false;
            var output = CaptureConsole(() =>
            {
                changed = AcceptanceStableSlotTestSupport.ExecuteWithIsolatedStableSlot(
                    ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal,
                    acceptanceVerifier: FakeAcceptanceVerifier.Passed(),
                    acceptanceCleanupContext: CreateIsolatedCleanupContext(repo));
            });

            Assert.True(changed);
            Assert.Equal(1, AcceptanceStableSlotTestSupport.LastSelectionCount);
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal), output);
            Assert.True(File.Exists(Path.Combine(repo, "queue-persist.txt")));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(HasAnyCleanupNeededRecord(repo, GoalWorktrees.WorktreePath(repo, goal.Id)));

            var reloaded = await stateRepository.LoadAsync();
            var reloadedGoal = reloaded.GetGoal(goal.Id);
            Assert.Null(reloadedGoal.LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_runs_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleSimpleGoalRunsAcceptsAndDefersWorkspaceCleanup()
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
                AcceptanceVerifier = fakeVerifier,
                CleanupContext = CreateIsolatedCleanupContext(repo),
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));

            Assert.Equal(goal.Id, context.CurrentGoal!.Id);
            Assert.Equal(1, kernel.Goals.Count);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_runs_scout_goal_accepts_and_defers_workspace_cleanup")]
    public void CliLifecycleGoalRunsScoutGoalAcceptsAndDefersWorkspaceCleanup()
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
                AcceptanceVerifier = fakeVerifier,
                CleanupContext = CreateIsolatedCleanupContext(repo),
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            Exception? invocationError = null;
            var output = CaptureConsole(() =>
            {
                invocationError = Record.Exception(() => CliCommandHandlers.Execute(
                    ["lifecycle-goal", "Ship a scout echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
            });
            Assert.True(invocationError is null, $"{invocationError}{Environment.NewLine}{output}");

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(
                [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
                goal.Tasks.Select(task => task.RequiredRole));
            Assert.True(goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage goal: created and activated.", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_records_landed_and_defers_workspace_cleanup")]
    public void CliGoalMarkLandedRecordsLandedAndDefersWorkspaceCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "landed.txt"), "landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            RunGit(repo, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var outputLines = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("Goal landed cleanup:", outputLines);
            Assert.Contains("cleanup: goal marked landed; cleanup-needed recorded", outputLines);
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
            var facts = new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: false);
            Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(goal, facts));

            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove")]
    public void CliGoalMarkLandedPassesRemainingCleanupBudgetToWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed budget goal", repo);
            RunGit(repo, "branch", GoalWorktrees.BranchName(goal.Id));
            var worktrees = new CapturingGoalWorktreeService(CleanupHooks.Build())
            {
                RemoveOverride = (_, _, _, _) => new GoalWorktreeRemoveResult(
                    "Workspace already clean; nothing to remove.",
                    null,
                    [],
                    null)
            };
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(eventWriter);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                EventWriter = eventWriter,
                Worktrees = worktrees,
                GoalMarkLandedElapsedMilliseconds = () => 9_000
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            Assert.True(output.Contains("cleanup: goal marked landed; cleanup-needed recorded", StringComparison.Ordinal));
            Assert.Null(worktrees.RemoveTimeoutMilliseconds);
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_records_landed_state_before_deferred_cleanup")]
    public void CliGoalMarkLandedRecordsLandedStateBeforeDeferredCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed deferred cleanup goal", repo);
            RunGit(repo, "branch", GoalWorktrees.BranchName(goal.Id));
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(eventWriter);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                EventWriter = eventWriter,
                GoalMarkLandedElapsedMilliseconds = () => CliCommandHandlers.GoalMarkLandedPromptTimeoutMilliseconds
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            Assert.Contains("cleanup: goal marked landed; cleanup-needed recorded", output);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:land" && e.Status == GoalOperationStatus.Completed);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:record" && e.Status == GoalOperationStatus.Completed);
            Assert.Contains(journal.LatestByOperation, e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Failed);
            Assert.DoesNotContain(journal.LatestByOperation, e =>
                e.Operation == "conductor:cleanup" && e.Status == GoalOperationStatus.Completed);

            var cleanupBackoff = TryGetCleanupBackoff(repo, goal.Id);
            Assert.NotNull(cleanupBackoff);
            Assert.StartsWith("remove:", cleanupBackoff!.Reason, StringComparison.Ordinal);
            var recovery = GoalRecoveryPlanner.Build(kernel, goal, repo, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            Assert.NotNull(recovery.CleanupBackoff);
            Assert.Contains(recovery.RecommendedActions, action =>
                action.Contains("workspace remove", StringComparison.Ordinal));
            var facts = new GoalLifecycleFacts(IsMerged: true, IsRecorded: true);
            Assert.Equal(GoalLifecycleState.Recorded, GoalLifecycle.ResolveState(goal, facts));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove")]
    public void CliGoalMarkLandedForceDeletesBranchKeptBySafeWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed force cleanup goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "force-landed.txt"), "force landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work not merged to main");

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(output.Contains("cleanup: goal marked landed; cleanup-needed recorded", StringComparison.Ordinal));
            Assert.NotNull(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(Directory.Exists(worktreePath));
            Assert.True(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
            Assert.NotNull(TryGetCleanupBackoff(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
