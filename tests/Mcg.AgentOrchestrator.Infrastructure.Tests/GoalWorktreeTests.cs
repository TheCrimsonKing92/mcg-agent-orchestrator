using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTests
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

    [Xunit.Fact(DisplayName = "GoalWorktrees_fast_forwards_goal_branch_on_merge")]
    public void GoalWorktreesFastForwardsGoalBranchOnMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal("Removed workspace and merged branch " + GoalWorktrees.BranchName(goalId) + ".", GoalWorktrees.Remove(repo, goalId));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
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

    private static void RunGit(string workingDirectory, params string[] arguments)
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
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
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
}
