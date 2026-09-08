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

public sealed class GoalWorktreeTestsRemoveCleanupStorage : GoalWorktreeTestBase
{

    [Xunit.Fact]
    public void FixtureCleanupUsesEachRepositoryRootForSameGoalArtifacts()
    {
        var firstRepo = CreateSeededRepository();
        var secondRepo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var firstRoot = new DotnetBuildStorageRoot(Path.Combine(firstRepo, ".orchestrator", "test-dotnet"));
            var secondRoot = new DotnetBuildStorageRoot(Path.Combine(secondRepo, ".orchestrator", "test-dotnet"));
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "fixture-first", storageRoot: firstRoot);
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "fixture-second", storageRoot: secondRoot);
            var secondMetadata = File.ReadAllBytes(second.LeaseMetadataPath!);

            Assert.True(RemoveWorktree(firstRepo, goalId).IsComplete);
            Assert.False(Directory.Exists(first.RootPath));
            Assert.Equal(secondMetadata, File.ReadAllBytes(second.LeaseMetadataPath!));

            Assert.True(RemoveWorktree(secondRepo, goalId).IsComplete);
            Assert.False(Directory.Exists(second.RootPath));
        }
        finally
        {
            DeleteDirectory(firstRepo);
            DeleteDirectory(secondRepo);
        }
    }

    [Xunit.Fact]
    public void FixtureCleanupPreservesExplicitStorageRootOverride()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var fallbackRoot = new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "test-dotnet"));
            var explicitRoot = new DotnetBuildStorageRoot(Path.Combine(repo, ".orchestrator", "explicit-dotnet"));
            var fallback = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "fixture-fallback", storageRoot: fallbackRoot);
            var explicitlyOwned = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "fixture-explicit", storageRoot: explicitRoot);
            var fallbackMetadata = File.ReadAllBytes(fallback.LeaseMetadataPath!);
            CleanupHooks.BuildStorageRoot = explicitRoot;

            Assert.True(RemoveWorktree(repo, goalId).IsComplete);
            Assert.False(Directory.Exists(explicitlyOwned.RootPath));
            Assert.Equal(fallbackMetadata, File.ReadAllBytes(fallback.LeaseMetadataPath!));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void BuildStorageRootIsAbsoluteAndNormalizedWithoutCreatingDirectories()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-root-value-" + Guid.NewGuid().ToString("N"));
        var root = new DotnetBuildStorageRoot(Path.Combine(path, "child", ".."));
        Assert.Equal(Path.GetFullPath(path), root.RootPath);
        Assert.False(Directory.Exists(path));
        Assert.Throws<ArgumentException>(() => new DotnetBuildStorageRoot("relative-root"));
        Assert.Throws<ArgumentException>(() => new DotnetBuildStorageRoot(" "));
        Assert.True(root.ContainsPath(Path.Combine(path, "build-slots", "build-0.lock")));
        Assert.False(root.ContainsPath(Path.Combine(path + "-sibling", "build-0.lock")));
        Assert.False(root.ContainsPath(Path.Combine(path, "..", "outside")));
        Assert.False(root.ContainsPath("relative-path"));
    }

    [Xunit.Fact]
    public void CliAcceptanceUsesConfiguredStorageWhenAnotherRootHasNoBuildPermits()
    {
        var repo = CreateSeededRepository();
        var held = new List<DotnetBuildEnvironmentLease>();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "CLI acceptance storage owner", repo);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "owned acceptance");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Owned acceptance");
            var rootA = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "storage-a"));
            var rootB = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "storage-b"));
            var ambientGoalRoot = DotnetBuildEnvironmentManager.GoalRoot(goal.Id);
            Assert.False(Directory.Exists(ambientGoalRoot));
            for (var index = 0; index < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; index++)
                held.Add(DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromSeconds(1), storageRoot: rootA));

            var verifier = FakeAcceptanceVerifier.Passed();
            CliExecutionContext Context(DotnetBuildStorageRoot root) => new(
                kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), goal)
            {
                AcceptanceVerifier = verifier,
                StableSlotAcquisitionTimeout = TimeSpan.FromMilliseconds(50),
                CleanupContext = new WorktreeCleanupContext(GoalWorktreeCleanupOptions.Default, workspace.OrchestratorDirectory, root)
            };

            var blocked = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], Context(rootA)));
            Assert.Contains("SLOTS_BUSY", blocked);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(GoalStatus.Verified, goal.Status);

            var accepted = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], Context(rootB)));
            Assert.Contains("Fast-forwarded", accepted);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            var environment = Assert.IsType<DotnetBuildEnvironment>(verifier.ObservedEnvironment);
            Assert.Equal(DotnetBuildEnvironmentManager.GoalRoot(goal.Id, rootB), environment.RootPath);
            var reportedRoot = Assert.Single(accepted.Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("root: ", StringComparison.Ordinal)));
            Assert.Equal($"root: present {environment.RootPath}", reportedRoot);
            Assert.True(rootB.ContainsPath(environment.ExecutionLockPath), "CLI acceptance ignored its configured storage namespace.");
            Assert.True(rootB.ContainsPath(environment.ArtifactsPath));
            Assert.False(Directory.Exists(ambientGoalRoot));
        }
        finally
        {
            foreach (var lease in held) lease.Dispose();
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void CliAcceptanceRejectsAndReleasesSelectorLeaseOutsideConfiguredStorage()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "CLI selector storage negative control", repo);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "negative control");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Selector negative control");
            var intended = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "intended"));
            var ignored = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "ignored"));
            var verifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(
                kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), goal)
            {
                AcceptanceVerifier = verifier,
                CleanupContext = new WorktreeCleanupContext(GoalWorktreeCleanupOptions.Default, workspace.OrchestratorDirectory, intended),
                // Deliberately ignore the configured namespace at the supported selector seam.
                StableSlotSelector = (timeout, onWait) => DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    timeout, onWait, storageRoot: ignored)
            };
            var error = Assert.Throws<InvalidOperationException>(() =>
                CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context)));
            Assert.Equal("Acceptance build lease is outside the configured build storage namespace.", error.Message);
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            for (var index = 0; index < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; index++)
                Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(index, ignored));
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CleanupStorageOwnerSeparatesSameGoalArtifactsAndDetectsIgnoredContext(bool ignoreStorageRoot)
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var rootA = new DotnetBuildStorageRoot(Path.Combine(repo, "storage-a"));
            var rootB = new DotnetBuildStorageRoot(Path.Combine(repo, "storage-b"));
            var first = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "owner-a", storageRoot: rootA);
            var second = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "owner-b", storageRoot: rootB);
            var survivorMetadata = File.ReadAllBytes(second.LeaseMetadataPath!);
            var ambientGoalRoot = DotnetBuildEnvironmentManager.GoalRoot(goalId);
            Assert.False(Directory.Exists(ambientGoalRoot));
            GoalWorktrees.Ensure(repo, goalId);

            var context = new WorktreeCleanupContext(GoalWorktreeCleanupOptions.Default, buildStorageRoot: rootA);
            var operationHooks = ignoreStorageRoot ? context.Hooks with { BuildStorageRoot = null } : context.Hooks;
            var result = GoalWorktrees.Remove(repo, goalId, hooks: operationHooks);
            Assert.True(result.IsComplete);

            Assert.Equal(survivorMetadata, File.ReadAllBytes(second.LeaseMetadataPath!));
            Assert.False(Directory.Exists(ambientGoalRoot));
            void AssertOwningRootRemoved() => Assert.False(Directory.Exists(first.RootPath));

            if (ignoreStorageRoot)
            {
                // Failure must specifically be the surviving owning root, never another root's damage.
                Assert.IsType<Xunit.Sdk.FalseException>(Xunit.Record.Exception(AssertOwningRootRemoved));
            }
            else
            {
                AssertOwningRootRemoved();
                Assert.True(GoalWorktrees.Remove(repo, goalId,
                    hooks: context.Hooks with { BuildStorageRoot = rootB }).IsComplete);
                Assert.False(Directory.Exists(second.RootPath));
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact]
    public void ExplicitStorageRootKeepsCreationPermitAndRunCleanupInOneNamespace()
    {
        var repo = CreateSeededRepository();
        try
        {
            var storageRoot = new DotnetBuildStorageRoot(Path.Combine(repo, "storage"));
            var goalId = GoalId.New();
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "owned-root", storageRoot: storageRoot);
            Assert.Equal(Path.Combine(storageRoot.RootPath, "goals", goalId.Value[..8].ToLowerInvariant()), environment.RootPath);
            Assert.Equal(Path.Combine(environment.RootPath, "artifacts"), environment.ArtifactsPath);
            Assert.StartsWith(environment.RootPath + Path.DirectorySeparatorChar, environment.LeaseMetadataPath!);
            Assert.Contains(environment.ArtifactsPath, environment.Arguments);
            var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero));
            using (acquired.Lease)
            {
                Assert.Equal(Path.Combine(storageRoot.RootPath, "build-slots"),
                    Path.GetDirectoryName(acquired.Lease.Environment.ExecutionLockPath));
            }

            Assert.Equal(environment.RootPath, DotnetBuildEnvironmentManager.InspectGoalLease(goalId, storageRoot).RootPath);
            var run = DotnetBuildEnvironmentManager.CreateAttempt(null, "owned-run", storageRoot: storageRoot);
            Assert.True(DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(run, storageRoot));
            Assert.False(Directory.Exists(run.RootPath));
            Assert.True(Directory.Exists(environment.RootPath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_persists_cleanup_needed_when_goal_artifacts_delete_fails")]
    public void GoalWorktreesRemovePersistsCleanupNeededWhenGoalArtifactsDeleteFails()
    {
        var repo = CreateSeededRepository();
        var originalLockHolders = CleanupHooks.FindLockHoldersForCleanup;
        var storageRoot = new DotnetBuildStorageRoot(Path.Combine(repo, "isolated-dotnet"));
        try
        {
            CleanupHooks.BuildStorageRoot = storageRoot;
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var buildEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "cleanup-needed", storageRoot: storageRoot);
            var lockedFile = Path.Combine(buildEnvironment.RootPath, "held-open.log");
            File.WriteAllText(lockedFile, "held");
            var lockReleased = false;
            CleanupHooks.FindLockHoldersForCleanup = _ => lockReleased
                ? []
                : [new WorktreeLockHolder(Environment.ProcessId, "dotnet", "held artifact root")];

            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = RemoveWorktree(repo, goalId);
            }
            lockReleased = true;

            if (OperatingSystem.IsWindows())
            {
                Assert.False(partial.IsComplete);
                Assert.Equal(buildEnvironment.RootPath, partial.LeftoverPath);
                Assert.Equal($"conduct {goalId.Value[..8].ToLowerInvariant()} --loop", partial.ResumeCommand);
                Assert.False(Directory.Exists(path));
                Assert.True(Directory.Exists(buildEnvironment.RootPath));
                Assert.True(HasCleanupNeededRecord(repo, buildEnvironment.RootPath, "remove:goal-artifacts:lock-held"));

                var retry = RemoveWorktree(repo, goalId);

                Assert.True(retry.IsComplete);
                Assert.False(Directory.Exists(buildEnvironment.RootPath));
                Assert.False(HasCleanupNeededRecord(repo, buildEnvironment.RootPath, "remove:goal-artifacts:lock-held"));
            }
            else
            {
                Assert.True(partial.IsComplete);
                Assert.False(Directory.Exists(buildEnvironment.RootPath));
            }
        }
        finally
        {
            CleanupHooks.FindLockHoldersForCleanup = originalLockHolders;
            DeleteDirectory(repo);
        }
    }
}
