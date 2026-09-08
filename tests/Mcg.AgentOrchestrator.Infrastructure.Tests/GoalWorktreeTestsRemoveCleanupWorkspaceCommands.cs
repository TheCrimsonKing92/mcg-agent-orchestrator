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

            var recoveringHooks = deferredHooks with { DeleteDirectory = configuredHooks.DeleteDirectory };
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
}
