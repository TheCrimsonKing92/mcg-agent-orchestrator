using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ReadOnlyRoleMainIntegrationGuardTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void PlannerDispatch_DirtyWorktree_HoldsWithDeveloperPathReasonAndStartsNoDispatch()
    {
        var repo = CreateAcceptanceCohortRepository();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Hold dirty Planner dispatch",
            [new TaskSpec(TaskId.New(), "Plan change", AgentRole.Planner),
             new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        try
        {
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(repo, "main.txt"), "main change");
            RunGit(repo, "add", "main.txt");
            RunGit(repo, "commit", "-m", "Main change");
            File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted");
            var initialHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            var expected = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default).Message;
            var dispatchStarted = false;
            string? escalation = null;
            var driver = ReadOnlyRoleMainIntegrationBeforeDispatchTests.CreateDriver(
                goal,
                repo,
                _ => dispatchStarted = true,
                reason => escalation = reason);

            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Escalated>(advance.Outcome);
            Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
            Assert.Equal(expected, escalation);
            Assert.False(dispatchStarted);
            Assert.Equal(initialHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Fact]
    public void PlannerDispatch_Conflict_HoldsWithRoleSubstitutedDeveloperReason()
    {
        var repo = CreateAcceptanceCohortRepository();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Hold conflicting Planner dispatch",
            [new TaskSpec(TaskId.New(), "Plan change", AgentRole.Planner),
             new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        try
        {
            File.WriteAllText(Path.Combine(repo, "shared.txt"), "base");
            RunGit(repo, "add", "shared.txt");
            RunGit(repo, "commit", "-m", "Shared base");
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktree, "shared.txt"), "goal change");
            RunGit(worktree, "add", "shared.txt");
            RunGit(worktree, "commit", "-m", "Goal change");
            var initialHead = RunGitOutput(worktree, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(repo, "shared.txt"), "main change");
            RunGit(repo, "add", "shared.txt");
            RunGit(repo, "commit", "-m", "Main change");
            var developerReason = ConductorDriver.IntegrateMainBeforeDeveloperDispatch(repo, goal, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default).Message;
            var dispatchStarted = false;
            string? escalation = null;
            var driver = ReadOnlyRoleMainIntegrationBeforeDispatchTests.CreateDriver(
                goal,
                repo,
                _ => dispatchStarted = true,
                reason => escalation = reason);

            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Escalated>(advance.Outcome);
            Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
            Assert.Equal(developerReason.Replace("Developer", "Planner", StringComparison.Ordinal), escalation);
            Assert.False(dispatchStarted);
            Assert.Equal(initialHead, RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short").Trim());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
