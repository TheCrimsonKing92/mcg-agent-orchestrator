using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: each case owns its git repository, kernel and event log.
public sealed class ConductorDriverTestsPreLandingNoRewrite
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreLandingDisjointMainAdvance_PreservesGoalHistoryAcrossGateVerdicts(bool gatePasses)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        using var fixture = new AdditiveConflictGitFixture([
            new("src/GoalOnly.cs", "base\n", "base\n", "goal\n"),
            new("src/MainOnly.cs", "base\n", "main\n", "base\n")], goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");
        var acceptanceCalls = 0;
        var driver = MakeDriver(
            rebaseOntoMain: current => GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, current.Id,
                ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath)),
            runAcceptance: _ =>
            {
                acceptanceCalls++;
                Assert.Equal(3, Git(fixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
                Assert.Equal("main\n", fixture.Read("src/MainOnly.cs"));
                return gatePasses;
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, acceptanceCalls);
        if (gatePasses)
            Assert.Equal("Landed: Landed", Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Description);
        else
            Assert.False(result.Outcome is ConductorAdvanceOutcome.Executed { Description: "Landed: Landed" });
        var branch = GoalWorktrees.BranchName(goal.Id);
        var ancestry = GitCli.Run(fixture.Repository, "merge-base", "--is-ancestor", fixture.OriginalHead, branch);
        Assert.Equal(0, ancestry.ExitCode);
        Assert.Equal(fixture.OriginalHead, Git(fixture.Repository, "rev-parse", $"{branch}^1").Trim());
    }

    [Fact]
    public void PreLandingNonAdditiveConflict_PreservesExactEscalationAndRestoresCheckout()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        using var fixture = new AdditiveConflictGitFixture([new(WidgetPath, "base\n", "main\n", "goal\n")], goal.Id);
        var logPath = Path.Combine(fixture.Repository, ".orchestrator", "logs", "conduct-events.log");
        const string expected = "pre-landing rebase conflict (src/Widget.cs); not confined to ratchet ceiling rows, so the conductor cannot resolve it; the operator integrates main into the goal branch";
        var escalations = new List<string>();
        var driver = MakeDriver(
            rebaseOntoMain: current => GoalBranchMainMerge.TryMergeMainIntoGoalBranch(fixture.Repository, current.Id,
                ConductorDriver.CreateAdditiveConflictMergeOptions(kernel, current, logPath)),
            runAcceptance: _ => throw new InvalidOperationException("A refused merge must not start acceptance."),
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(expected, Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason);
        Assert.Equal(expected, Assert.Single(escalations));
        Assert.StartsWith("pre-landing rebase conflict (src/Widget.cs)", escalations[0]);
        Assert.DoesNotContain("workspace rebase", escalations[0]);
        fixture.AssertRestored();
    }
}
