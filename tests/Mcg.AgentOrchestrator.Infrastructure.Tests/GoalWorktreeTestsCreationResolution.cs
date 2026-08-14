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


public sealed class GoalWorktreeTestsCreationResolution : GoalWorktreeTestBase
{
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

    [Xunit.Fact(DisplayName = "GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean")]
    public void GoalWorktreesCommitOnBehalfAfterWorkerCommitLeavesWorktreeClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Commit residual dirty worktree", [taskSpec]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, dispatchedAt));

            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Worker commit");
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");

            var logs = Path.Combine(repo, "logs");
            Directory.CreateDirectory(logs);
            var stdout = Path.Combine(logs, "developer.out.log");
            var stderr = Path.Combine(logs, "developer.err.log");
            var exit = Path.Combine(logs, "developer.exit.txt");
            File.WriteAllText(stdout, "Committed implementation.");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, dispatchedAt, null, null));

            new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);

            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.LastVerification!.ExitCode);
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short"));
            Assert.Equal("Developer task.: Committed implementation.", RunGitOutput(worktree, "log", "-1", "--pretty=%s"));
            Assert.Equal("seed.txt", RunGitOutput(worktree, "show", "--name-only", "--pretty=", "HEAD"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
