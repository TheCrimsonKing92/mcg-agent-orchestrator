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

public sealed class GoalWorktreeTestsRemoveCleanupTerminal : GoalWorktreeTestBase
{

    [Xunit.Fact(DisplayName = "GoalWorktrees_terminal_remove_deletes_long_path_and_prunes_registration")]
    public void GoalWorktreesTerminalRemoveDeletesLongPathAndPrunesRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scratchRoot = Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-long-wt", Guid.NewGuid().ToString("n"));
        var repo = Path.Combine(scratchRoot, "repo");
        var originalWorktreeRemove = CleanupHooks.RunWorktreeRemove;
        var originalWorktreePrune = CleanupHooks.RunWorktreePrune;

        try
        {
            var shutdownRequests = CaptureBuildServerShutdownRequests();
            Directory.CreateDirectory(repo);
            RunGit(repo, "init");
            RunGit(repo, "config", "user.email", "tests@example.com");
            RunGit(repo, "config", "user.name", "Worktree Tests");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Seed");

            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Exercise long-path cleanup.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Long-path cleanup", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var deepPath = Path.Combine(worktree, "long-path-segment-0123456789");
            while (deepPath.Length <= 260)
            {
                deepPath = Path.Combine(deepPath, "long-path-segment-0123456789");
            }

            var extendedDeepPath = @"\\?\" + deepPath;
            Directory.CreateDirectory(extendedDeepPath);
            Assert.True(Directory.Exists(extendedDeepPath));
            kernel.CancelGoal(goal.Id, "Test terminal cleanup.");
            string? removalPath = null;
            var pruneCalls = 0;
            CleanupHooks.RunWorktreeRemove = (_, _, path, force) =>
            {
                removalPath = path;
                Assert.True(force);
                return new GitCli.GitResult(1, string.Empty, "simulated git long-path refusal");
            };
            CleanupHooks.RunWorktreePrune = (executionDirectory, timeout, expireNow) =>
            {
                pruneCalls++;
                Assert.True(expireNow);
                return GitCli.Run(executionDirectory, timeout, "worktree", "prune", "--expire", "now");
            };

            var result = RemoveTerminalWorktree(repo, goal.Id, kernel);

            Assert.True(result.IsComplete, result.Message);
            Assert.True(removalPath?.StartsWith(@"\\?\", StringComparison.Ordinal) is true, removalPath);
            Assert.Equal(1, pruneCalls);
            Assert.False(Directory.Exists(worktree));
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.DoesNotContain(
                NormalizePath(worktree),
                RunGitOutput(repo, "worktree", "list", "--porcelain"),
                StringComparison.OrdinalIgnoreCase);
            AssertBuildServerShutdownRequests(shutdownRequests, worktree);
        }
        finally
        {
            CleanupHooks.RunWorktreeRemove = originalWorktreeRemove;
            CleanupHooks.RunWorktreePrune = originalWorktreePrune;
            _ = CleanupHooks.DeleteDirectory(scratchRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_terminal_remove_reports_unsafe_prefix_collision_without_throwing")]
    public void GoalWorktreesTerminalRemoveReportsUnsafePrefixCollisionWithoutThrowing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var terminalId = new GoalId("12345678aaaaaaaaaaaaaaaaaaaaaaaa");
            var activeId = new GoalId("12345678bbbbbbbbbbbbbbbbbbbbbbbb");
            var terminal = kernel.CreateGoal(
                terminalId,
                "Terminal collision",
                [new TaskSpec(TaskId.New(), "Terminal work", AgentRole.Developer)]);
            var active = kernel.CreateGoal(
                activeId,
                "Active collision",
                [new TaskSpec(TaskId.New(), "Active work", AgentRole.Developer)]);
            kernel.ActivateGoal(terminal.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, terminal.Id);
            kernel.CancelGoal(terminal.Id, "Terminal collision test.");

            var result = RemoveTerminalWorktree(repo, terminal.Id, kernel);

            Assert.False(result.IsComplete);
            Assert.Contains("refused unsafe target", result.Message, StringComparison.Ordinal);
            Assert.Contains($"non-terminal goal {active.Id.Value[..8]} (Active)", result.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public async Task RemoveTerminalKeepsConcurrentPublicOperationsAndAttentionStoresIsolated()
    {
        var repositories = new List<string>();
        using var rendezvous = new Barrier(2);
        try
        {
            (string Repo, string Path, Goal Goal, AgentOrchestratorKernel Kernel,
                GoalWorktreeCleanupHooks Hooks, ConcurrentBag<string> Deletes) CreateOperation()
            {
                var repo = CreateSeededRepository();
                repositories.Add(repo);
                var kernel = new AgentOrchestratorKernel();
                var goal = kernel.CreateGoal("Concurrent terminal cleanup",
                    [new TaskSpec(TaskId.New(), "Leave owned residue.", AgentRole.Developer)]);
                kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                var path = GoalWorktrees.Ensure(repo, goal.Id);
                File.Delete(Path.Combine(path, ".git"));
                RunGit(repo, "worktree", "prune");
                kernel.CancelGoal(goal.Id, "Exercise isolated terminal cleanup.");
                var deletes = new ConcurrentBag<string>();
                var entered = 0;
                var hooks = GoalWorktreeCleanupHooks.ForConfiguration(
                    new GoalWorktreeCleanupOptions(TimeSpan.FromMinutes(5), 1, TimeSpan.FromDays(1)),
                    Path.Combine(repo, ".orchestrator")) with
                {
                    DeleteDirectory = candidate =>
                    {
                        deletes.Add(candidate);
                        if (Interlocked.Exchange(ref entered, 1) == 0 &&
                            !rendezvous.SignalAndWait(TimeSpan.FromSeconds(30)))
                        {
                            throw new TimeoutException("Both public cleanup operations must overlap.");
                        }
                        return false;
                    },
                    ResetSandboxAcl = (_, _) => { },
                    BuildServerShutdown = (_, _) => { },
                    FindLockHoldersForCleanup = _ => [],
                    CleanupWarningSink = _ => { }
                };
                return (repo, path, goal, kernel, hooks, deletes);
            }

            var first = CreateOperation();
            var second = CreateOperation();
            var results = await Task.WhenAll(
                Task.Run(() => GoalWorktrees.RemoveTerminal(first.Repo, first.Goal.Id, first.Kernel, first.Hooks)),
                Task.Run(() => GoalWorktrees.RemoveTerminal(second.Repo, second.Goal.Id, second.Kernel, second.Hooks)));

            Assert.All(results, result => Assert.False(result.IsComplete));
            Assert.Contains(first.Path, first.Deletes);
            Assert.DoesNotContain(second.Path, first.Deletes);
            Assert.Contains(second.Path, second.Deletes);
            Assert.DoesNotContain(first.Path, second.Deletes);
            foreach (var operation in new[] { first, second })
            {
                Assert.NotNull(GoalWorktrees.TryGetCleanupBackoff(operation.Repo, operation.Goal.Id, operation.Hooks));
                var store = CollaborationItemStore.ForDirectory(Path.Combine(operation.Repo, ".orchestrator"));
                var attention = Assert.Single(await store.GetAttentionQueueAsync());
                Assert.Contains($"workspace remove {operation.Goal.Id.Value[..8]}", attention.Body, StringComparison.Ordinal);
                var other = operation.Goal.Id == first.Goal.Id ? second : first;
                Assert.DoesNotContain(other.Goal.Id.Value[..8], attention.Body, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var repo in repositories)
            {
                DeleteDirectory(repo);
            }
        }
    }

    [Xunit.Fact(DisplayName = "GoalAbandon_removes_terminal_worktree_in_one_cleanup_cycle")]
    public void GoalAbandonRemovesTerminalWorktreeInOneCleanupCycle()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Abandon clean worktree.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Abandon cleanup", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            _ = GoalWorktrees.Ensure(repo, goal.Id);

            var result = GoalAbandonPlanner.Apply(
                kernel,
                goal,
                OrchestratorWorkspace.ForDirectory(repo),
                "No longer required.",
                CreateIsolatedCleanupContext(repo).Hooks);

            Assert.True(result.CanApply);
            Assert.Equal(GoalStatus.Cancelled, kernel.GetGoal(goal.Id).Status);
            var worktreesRoot = Path.Combine(repo, GoalWorktrees.DirectoryName);
            Assert.Empty(Directory.Exists(worktreesRoot)
                ? Directory.EnumerateDirectories(worktreesRoot)
                : []);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    // abandon-goal inspects and deletes a goal build lease. Both the reported plan and the
    // deletion must use the caller's build storage root; reverting to the process default would
    // leave the owned lease in place and describe a namespace the command does not own.
    [Xunit.Fact(DisplayName = "GoalAbandon_cleans_only_its_owned_build_lease_root")]
    public void GoalAbandonCleansOnlyItsOwnedBuildLeaseRoot()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal(
                "Abandon owned build lease",
                [new TaskSpec(TaskId.New(), "Abandon clean worktree.", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            _ = GoalWorktrees.Ensure(repo, goal.Id);
            var cleanupHooks = CreateIsolatedCleanupContext(repo).Hooks;
            var ownedLeaseRoot = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, cleanupHooks.BuildStorageRoot);
            Directory.CreateDirectory(ownedLeaseRoot);
            File.WriteAllText(
                Path.Combine(ownedLeaseRoot, "owned-marker.txt"),
                "abandon must remove its owned lease");
            var foreignRoot = new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "foreign-dotnet"));
            var foreignLeaseRoot = DotnetBuildEnvironmentManager.GoalRoot(goal.Id, foreignRoot);
            Directory.CreateDirectory(foreignLeaseRoot);
            var foreignMarker = Path.Combine(foreignLeaseRoot, "foreign-marker.txt");
            File.WriteAllText(foreignMarker, "preserve this independent owner");

            var preview = GoalAbandonPlanner.Build(kernel, goal, workspace, "No longer required.", cleanupHooks);

            Assert.Contains(preview.Steps, step =>
                step.Kind == GoalAbandonStepKind.BuildLease &&
                step.Disposition == GoalAbandonDisposition.Apply);
            Assert.Contains(preview.RetentionPlan.Items, item =>
                item.Kind == RetentionArtifactKind.BuildLease &&
                string.Equals(item.Path, ownedLeaseRoot, StringComparison.OrdinalIgnoreCase));

            var applied = GoalAbandonPlanner.Apply(kernel, goal, workspace, "No longer required.", cleanupHooks);

            Assert.True(applied.CanApply);
            Assert.Equal(GoalStatus.Cancelled, kernel.GetGoal(goal.Id).Status);
            Assert.False(Directory.Exists(ownedLeaseRoot));
            Assert.Equal("preserve this independent owner", File.ReadAllText(foreignMarker));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void RemoveSupersededTerminal_ChangedTip_KeepsBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Changed superseded branch", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", repo, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel.CompleteGoal(goal.Id, "Completed before superseded cleanup.");
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            var branch = GoalWorktrees.BranchName(goal.Id);
            File.WriteAllText(Path.Combine(path, "first.txt"), "first");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "First commit");
            var checkedTip = RunGitOutput(path, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(path, "second.txt"), "second");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Changed after equivalence check");
            var changedTip = RunGitOutput(path, "rev-parse", "HEAD").Trim();

            var result = GoalWorktrees.RemoveSupersededTerminal(
                repo,
                goal.Id,
                kernel,
                checkedTip,
                hasRegisteredWorktree: true,
                hasBranch: true);

            Assert.False(result.IsComplete);
            Assert.Contains("branch deletion failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(path));
            Assert.True(BranchExists(repo, branch));
            Assert.Equal(changedTip, RunGitOutput(repo, "rev-parse", branch).Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
