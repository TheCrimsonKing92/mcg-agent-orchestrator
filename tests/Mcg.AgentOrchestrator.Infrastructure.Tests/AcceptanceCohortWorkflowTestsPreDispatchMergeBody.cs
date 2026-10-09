using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortWorkflowTestsPreDispatchMergeBody : AcceptanceCohortWorkflowTests
{
    [Xunit.Theory]
    [Xunit.InlineData("# Readable merge bodies\nDetails", "Goal: Readable merge bodies")]
    [Xunit.InlineData("Fix Integrate goal/abc handling", "")]
    public void PreDispatchMergePreservesSubjectAndWritesSafeTitleBody(string objective, string expectedBody)
    {
        var repo = CreateAcceptanceCohortRepository();
        var goal = new Goal(
            GoalId.New(), objective,
            [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        try
        {
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "goal.txt"), "goal change");
            RunGit(worktree, "add", "goal.txt");
            RunGit(worktree, "commit", "-m", "Goal change");
            File.WriteAllText(Path.Combine(repo, "main.txt"), "main change");
            RunGit(repo, "add", "main.txt");
            RunGit(repo, "commit", "-m", "Main change");

            var result = ConductorDriver.IntegrateMainBeforeDispatch(repo, goal, AgentRole.Developer, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);

            Xunit.Assert.Equal(DeveloperBranchIntegrationStatus.Integrated, result.Status);
            Xunit.Assert.Equal(
                $"Integrate main into {GoalWorktrees.BranchName(goal.Id)} before Developer dispatch",
                RunGitOutput(worktree, "log", "-1", "--format=%s").Trim());
            Xunit.Assert.Equal(expectedBody, RunGitOutput(worktree, "log", "-1", "--format=%b").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
