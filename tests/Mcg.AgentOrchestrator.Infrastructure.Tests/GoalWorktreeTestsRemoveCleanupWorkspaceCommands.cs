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

public sealed class GoalWorktreeTestsRemoveCleanupWorkspaceCommands : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(GitCli.DefaultTimeoutMilliseconds)]
    public void CliWorktreeRemovalUsesItsContextCleanupOwner(int? timeout)
    {
        var repo = CreateSeededRepository();
        var otherRepo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var shutdownRequests = new List<(string Path, int Budget)>();
            var hooks = new GoalWorktreeCleanupHooks
            {
                BuildServerShutdown = (directory, budget) => shutdownRequests.Add((directory, budget))
            };
            var owner = CreateCleanupOwner(repo, hooks);
            var otherGoalId = GoalId.New();
            var otherPath = GoalWorktrees.Ensure(otherRepo, otherGoalId);
            File.Delete(Path.Combine(otherPath, ".git"));
            RunGit(otherRepo, "worktree", "prune");
            var otherShutdownRequests = new List<(string Path, int Budget)>();
            var otherOwner = CreateCleanupOwner(otherRepo, hooks with
            {
                BuildServerShutdown = (directory, budget) => otherShutdownRequests.Add((directory, budget))
            });

            var result = owner.Worktrees.Remove(repo, goalId, gitTimeoutMilliseconds: timeout);
            Assert.Empty(otherShutdownRequests);
            var otherResult = otherOwner.Worktrees.Remove(otherRepo, otherGoalId, gitTimeoutMilliseconds: timeout);

            Assert.True(result.IsComplete, result.Message);
            Assert.True(otherResult.IsComplete, otherResult.Message);
            Assert.False(Directory.Exists(path));
            Assert.False(Directory.Exists(otherPath));
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(goalId)));
            Assert.False(BranchExists(otherRepo, GoalWorktrees.BranchName(otherGoalId)));
            var request = Assert.Single(shutdownRequests);
            Assert.Equal(path, request.Path);
            Assert.InRange(request.Budget, 1, timeout ?? GitCli.DefaultTimeoutMilliseconds);
            var otherRequest = Assert.Single(otherShutdownRequests);
            Assert.Equal(otherPath, otherRequest.Path);
            Assert.InRange(otherRequest.Budget, 1, timeout ?? GitCli.DefaultTimeoutMilliseconds);
        }
        finally
        {
            DeleteDirectory(repo);
            DeleteDirectory(otherRepo);
        }
    }

    [Xunit.Fact]
    public void CliWorktreeCreationUsesItsContextToClearAnOrphan()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.WorktreePath(repo, goalId);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "orphan.txt"), "left by an interrupted creation");
            var deletedPaths = new List<string>();
            var hooks = new GoalWorktreeCleanupHooks
            {
                DeleteDirectoryForCleanup = directory =>
                {
                    deletedPaths.Add(directory);
                    return GoalWorktrees.DeleteDirectoryWithReason(directory);
                }
            };

            var created = CreateCleanupOwner(repo, hooks).Worktrees.Ensure(repo, goalId);

            Assert.Equal(path, created);
            Assert.Equal(path, Assert.Single(deletedPaths));
            Assert.False(File.Exists(Path.Combine(path, "orphan.txt")));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goalId));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static CliExecutionContext CreateCleanupOwner(string repo, GoalWorktreeCleanupHooks hooks) =>
        new(new AgentOrchestratorKernel(), OrchestratorWorkspace.ForDirectory(repo),
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), null)
        {
            CleanupContext = CreateIsolatedCleanupContext(repo, hooks)
        };

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_force_terminal_cleanup_bypasses_escalated_backoff")]
    public void CliWorkspaceRemoveForceTerminalCleanupBypassesEscalatedBackoff()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Forced terminal cleanup",
                [new TaskSpec(TaskId.New(), "Leave terminal residue.", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            kernel.CancelGoal(goal.Id, "Force cleanup test.");
            var configuredHooks = GoalWorktreeCleanupHooks.ForConfiguration(
                new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(5), 1, TimeSpan.FromDays(1)),
                Path.Combine(repo, ".orchestrator"));
            var deferredHooks = configuredHooks with
            {
                DeleteDirectory = _ => false,
                ResetSandboxAcl = (_, _) => { },
                BuildServerShutdown = (_, _) => { },
                FindLockHoldersForCleanup = _ => []
            };

            var deferred = GoalWorktrees.RemoveTerminal(repo, goal.Id, kernel, deferredHooks);
            Assert.False(deferred.IsComplete);
            Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id, deferredHooks));
            var attentionStore = CollaborationItemStore.ForDirectory(Path.Combine(repo, ".orchestrator"));
            var attention = Assert.Single(attentionStore.GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Contains(
                $"workspace remove {goal.Id.Value[..8]} --force-terminal-cleanup",
                attention.Body,
                StringComparison.Ordinal);

            var shutdownRequests = new List<(string Path, int Budget)>();
            var recoveringHooks = deferredHooks with
            {
                DeleteDirectory = configuredHooks.DeleteDirectory,
                BuildServerShutdown = (directory, budget) => shutdownRequests.Add((directory, budget))
            };
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                CleanupContext = new WorktreeCleanupContext(recoveringHooks)
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["workspace", "remove", goal.Id.Value[..8], "--force-terminal-cleanup"],
                context));

            Assert.False(Directory.Exists(path));
            var shutdown = Assert.Single(shutdownRequests);
            Assert.Equal(path, shutdown.Path);
            Assert.InRange(shutdown.Budget, 1, GitCli.DefaultTimeoutMilliseconds);
            Assert.Null(GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id, configuredHooks));
            Assert.Empty(attentionStore.GetAttentionQueueAsync().GetAwaiter().GetResult());
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

    [Xunit.Fact]
    public async Task Keyed_goal_replay_keeps_one_workspace_and_clean_repository()
    {
        var repo = CreateSeededRepository();
        try
        {
            SeedLocalSkillCatalog(repo);
            RunGit(repo, "add", ".agents/skills");
            RunGit(repo, "commit", "-m", "Seed local skills");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = SeedSpecRefiner(workspace);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var intake = new[] { "simple-goal", "Keyed workspace goal", "--request-key", "workspace-key" };

            _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                intake, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
            var goal = Assert.Single((await repository.LoadAsync()).Goals);
            var kernel = await repository.LoadAsync();
            currentGoal = kernel.Goals.Single();

            _ = CliCommandDispatcher.ExecuteCommand(
                ["workspace", "create"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            var worktreePath = GoalWorktrees.TryResolve(repo, goal.Id);
            Assert.NotNull(worktreePath);

            _ = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                intake, repository, workspace, ref agents, providers, ref profiles, ref currentGoal));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Single((await repository.LoadAsync()).Goals);

            _ = CliCommandDispatcher.ExecuteCommand(
                ["workspace", "remove"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            var repositoryStatus = RunGitOutput(repo, "status", "--short");
            Assert.True(
                string.IsNullOrWhiteSpace(repositoryStatus),
                $"Repository remained dirty after keyed replay cleanup:{Environment.NewLine}{repositoryStatus}");
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_repairs_landed_cleaned_stale_acceptance_failure")]
    public void CliWorkspaceRemoveRepairsLandedCleanedStaleAcceptanceFailure()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove recovery", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["acceptance evidence blocked"]);
            GoalOperationJournal.Completed(repo, goal, "acceptance", "Acceptance passed and merge completed.");
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var order = new List<string>();
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = new RecordingGoalLifecycleEventWriter(order)
            };

            CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Null(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.Contains("remove-worktree", order);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_persists_provider_session_retirement")]
    public async Task CliWorkspaceRemovePersistsProviderSessionRetirement()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Workspace remove session retirement", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "session-retirement.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Session retirement goal");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                worktreePath,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                ProviderSessionId: "workspace-session",
                WorktreeHeadSha: "abc123",
                DirtyStateHash: "dirty-hash"));
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            GoalOperationJournal.Completed(repo, goal, "acceptance", "Acceptance passed and merge completed.");

            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = false;
            CaptureConsole(() =>
            {
                changed = CliPersistentStateRunner.ExecuteCommand(
                    ["workspace", "remove", goal.Id.Value[..8]],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
            });

            Assert.True(changed);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            var reloadedTask = (await stateRepository.LoadAsync()).GetTask(goal.Id, task.Id);
            Assert.Equal("workspace-session", reloadedTask.LastDispatch!.ProviderSessionId);
            Assert.NotNull(reloadedTask.LastDispatch.ProviderSessionRetiredAt);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_keeps_stale_acceptance_failure_without_landing_evidence")]
    public void CliWorkspaceRemoveKeepsStaleAcceptanceFailureWithoutLandingEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove no landing recovery", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["acceptance evidence blocked"]);
            _ = GoalWorktrees.Ensure(repo, goal.Id);
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.DoesNotContain("Acceptance repaired:", output);
            Assert.NotNull(kernel.GetGoal(goal.Id).LatestAcceptanceFailure);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_does_not_complete_verified_goal_without_landing_evidence")]
    public void CliWorkspaceRemoveDoesNotCompleteVerifiedGoalWithoutLandingEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove no terminal evidence", repo);
            var order = new List<string>();
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = new RecordingGoalLifecycleEventWriter(order)
            };

            CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(goal.Id).Status);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.DoesNotContain("remove-worktree", order);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation == "workspace:remove" &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.DoesNotContain(journal.LatestByOperation, entry =>
                entry.Operation == "acceptance" &&
                entry.Status == GoalOperationStatus.Completed);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_prints_cleanup_backoff_skip_until")]
    public void CliWorkspaceRemovePrintsCleanupBackoffSkipUntil()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Workspace remove backoff", repo);
            var path = Path.Combine(repo, GoalWorktrees.DirectoryName, goal.Id.Value[..8]);
            var skipUntil = DateTimeOffset.Parse("2026-07-02T05:01:00Z");
            var worktrees = new CapturingGoalWorktreeService(CleanupHooks.Build())
            {
                RemoveOverride = (_, _, _, _) => new GoalWorktreeRemoveResult(
                    "Workspace cleanup deferred by cleanup-needed backoff.",
                    path,
                    [],
                    $"conduct {goal.Id.Value[..8]} --loop",
                    CleanupBackoff: new GoalWorktreeCleanupBackoff(
                        "remove:cleanup-budget-exhausted",
                        skipUntil,
                        TimeSpan.FromMinutes(1)))
            };
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                Worktrees = worktrees
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["workspace", "remove", goal.Id.Value[..8]], context));

            Assert.Contains("Cleanup backoff:", output);
            Assert.Contains("reason=remove:cleanup-budget-exhausted", output);
            Assert.Contains("skip_until_utc=2026-07-02T05:01:00.0000000+00:00", output);
            Assert.Contains("remaining_wait=00:01:00", output);
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

            var ex = Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
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

    // The deferred cleanup-debt write applies escalation threshold, escalated retry interval,
    // clock and attention-store directory. Dropping the command's cleanup owner replaces all of
    // them with record literals, so this asserts the configured values reached the durable record.
    [Xunit.Fact(DisplayName = "Cli_deferred_cleanup_debt_applies_the_owning_cleanup_configuration")]
    public void CliDeferredCleanupDebtAppliesTheOwningCleanupConfiguration()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Owned deferred cleanup configuration", repo);
            RunGit(repo, "branch", GoalWorktrees.BranchName(goal.Id));
            var now = DateTimeOffset.Parse("2026-09-12T04:00:00Z", CultureInfo.InvariantCulture);
            var attentionDirectory = Path.Combine(repo, ".orchestrator-owned-attention");
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var ownedHooks = GoalWorktreeCleanupHooks.ForConfiguration(
                // Escalate on the first failure and hold for a distinctly non-default interval.
                new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(5), 1, TimeSpan.FromDays(7)),
                attentionDirectory,
                new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "test-dotnet"))) with
            {
                CleanupUtcNow = () => now,
                FindLockHoldersForCleanup = _ => [],
                CleanupWarningSink = warnings.Add
            };
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                CleanupContext = new WorktreeCleanupContext(ownedHooks)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            var backoff = GoalWorktrees.TryGetCleanupBackoff(repo, goal.Id, ownedHooks);
            Assert.NotNull(backoff);
            Assert.Equal("remove:goal-mark-landed-deferred", backoff!.Reason);
            Assert.Equal(now.AddDays(7), backoff.SkipUntilUtc);
            Assert.Contains($"skip_until_utc={now.AddDays(7):O}", output, StringComparison.Ordinal);
            Assert.Contains(warnings, warning => warning.Operation == "cleanup-debt-escalated");

            var ownedAttention = Assert.Single(CollaborationItemStore.ForDirectory(attentionDirectory)
                .GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Contains("cleanup-debt escalation", ownedAttention.Body, StringComparison.Ordinal);
            Assert.Contains(
                $"workspace remove {goal.Id.Value[..8]}",
                ownedAttention.Body,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                    .GetAttentionQueueAsync().GetAwaiter().GetResult(),
                item => item.Body.Contains("cleanup-debt escalation", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    // Listing cleanup debt reports the same counts under any hook record, so the observable
    // ownership difference is the failure sink: an unreadable cleanup-state store must warn
    // through the command's own sink instead of the process-default console writer.
    [Xunit.Fact(DisplayName = "Cli_goals_listing_reports_cleanup_debt_read_failure_to_its_owner")]
    public void CliGoalsListingReportsCleanupDebtReadFailureToItsOwner()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Owned cleanup-debt listing",
                [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                CleanupContext = CreateIsolatedCleanupContext(
                    repo,
                    new GoalWorktreeCleanupHooks { CleanupWarningSink = warnings.Add })
            };
            // The cleanup-state store is the workspace state database; replacing it with a
            // non-database file makes the debt read fail deterministically.
            File.WriteAllText(workspace.SqliteStatePath, "not a sqlite database");

            _ = CaptureConsole(() => CliCommandHandlers.Execute(["goals"], context));

            var warning = Assert.Single(warnings);
            Assert.Equal("cleanup-status:read", warning.Operation);
            Assert.Equal(Path.GetFullPath(repo), Path.GetFullPath(warning.Path));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    // Conductor workspace creation retries worktree-add after clearing an orphan directory.
    // That deletion is cleanup, so it must run through the conductor's own cleanup owner.
    [Xunit.Fact(DisplayName = "Conductor_workspace_create_clears_an_orphan_through_its_cleanup_owner")]
    public void ConductorWorkspaceCreateClearsAnOrphanThroughItsCleanupOwner()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Conductor orphan clearing",
                [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var orphanPath = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(orphanPath);
            File.WriteAllText(
                Path.Combine(orphanPath, "orphan.txt"),
                "left by an interrupted conductor workspace creation");
            var clearedPaths = new List<string>();
            var cleanupHooks = CreateIsolatedCleanupContext(repo).Hooks with
            {
                DeleteDirectoryForCleanup = directory =>
                {
                    clearedPaths.Add(directory);
                    return GoalWorktrees.DeleteDirectoryWithReason(directory);
                }
            };
            var driver = new ConductorDriver(
                kernel,
                workspace,
                FakeAcceptanceVerifier.Passed(),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupHooks);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Created, executed.FromState);
            Assert.Equal(orphanPath, Assert.Single(clearedPaths));
            Assert.False(File.Exists(Path.Combine(orphanPath, "orphan.txt")));
            Assert.Equal(orphanPath, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
