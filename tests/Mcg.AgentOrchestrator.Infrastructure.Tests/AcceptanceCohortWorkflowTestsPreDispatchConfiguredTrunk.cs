using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: git refs and project state belong to this test's temporary directories.
public sealed class AcceptanceCohortWorkflowTestsPreDispatchConfiguredTrunk : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(null, "main")]
    [InlineData("", "main")]
    [InlineData(" ", "main")]
    [InlineData(" master ", "master")]
    [InlineData(" refs/heads/master ", "refs/heads/master")]
    public void BranchResolutionPreservesDefaultAndTrimsConfiguredNames(string? configured, string expected)
    {
        Assert.Equal(expected, TrunkBranchName.Resolve(configured));
    }

    [Fact]
    public void PreDispatchIntegrationUsesMasterWhenMainDoesNotExist()
    {
        var repo = CreateAcceptanceCohortRepository();
        var dataRoot = SharedTestSupport.CreateTempDirectory();
        try
        {
            RunGit(repo, "branch", "-M", "master");
            Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "refs/heads/main").ExitCode);
            var workspace = OrchestratorWorkspace.ForProject("alpha", repo,
                integrationBranch: "master", dataRootDirectory: dataRoot);
            var goal = new Goal(GoalId.New(), "Integrate configured trunk",
                [new TaskSpec(TaskId.New(), "Plan change", AgentRole.Planner)]);
            var worktree = GoalWorktrees.Ensure(repo, goal.Id, trunkBranch: workspace.IntegrationBranch);

            var current = ConductorDriver.IntegrateMainBeforeDispatch(
                workspace.ExecutionDirectory, goal, AgentRole.Planner, workspace.IntegrationBranch);
            Assert.Equal(DeveloperBranchIntegrationStatus.Current, current.Status);
            Assert.Contains("current with master", current.Message, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(worktree, "goal.txt"), "goal change");
            RunGit(worktree, "add", "goal.txt");
            RunGit(worktree, "commit", "-m", "Goal change");
            File.WriteAllText(Path.Combine(repo, "trunk.txt"), "trunk change");
            RunGit(repo, "add", "trunk.txt");
            RunGit(repo, "commit", "-m", "Trunk change");
            var master = RunGitOutput(repo, "rev-parse", "master").Trim();

            var integrated = ConductorDriver.IntegrateMainBeforeDispatch(
                workspace.ExecutionDirectory, goal, AgentRole.Planner, workspace.IntegrationBranch);
            Assert.Equal(DeveloperBranchIntegrationStatus.Integrated, integrated.Status);
            Assert.Contains("integrated master", integrated.Message, StringComparison.Ordinal);
            Assert.Equal($"Integrate master into {GoalWorktrees.BranchName(goal.Id)} before Planner dispatch",
                RunGitOutput(worktree, "log", "-1", "--format=%s").Trim());
            Assert.Equal(0, GitCli.Run(worktree, "merge-base", "--is-ancestor", master, "HEAD").ExitCode);
            Assert.False(GitCli.IsWorktreeDirty(worktree));
            Assert.DoesNotContain(" main ", current.Message + integrated.Message, StringComparison.Ordinal);
            Assert.NotEqual(0, GitCli.Run(repo, "rev-parse", "--verify", "refs/heads/main").ExitCode);
        }
        finally
        {
            DeleteDirectory(repo);
            SharedTestSupport.RemoveTempDirectory(dataRoot);
        }
    }
}
