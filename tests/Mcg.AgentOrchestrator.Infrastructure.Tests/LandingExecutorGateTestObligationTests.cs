using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real Git integration: uses the same isolated fixture and collection as other landing tests.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorGateTestObligationTests
{
    [Fact]
    public void MissingSelector_HoldsAtRungTwoBeforeMutation()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/obligation.txt");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["MissingTests"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                [("OtherTests.Row", "Passed")]);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(2, escalation.Decision!.Rung);
            Assert.Contains(Assert.Single(goal.GateTestObligations).Id[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Contains("selector-not-found", escalation.Reason, StringComparison.Ordinal);
            Assert.Contains("MissingTests", escalation.Reason, StringComparison.Ordinal);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SatisfiedOrPriorVersionObligation_LandsWithoutWait(bool reviseBrief)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/obligation.txt");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["AlphaTests", "BetaTests.Row"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            if (reviseBrief) kernel.ReviseGoalBrief(goal.Id, "Revised gate evidence");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                reviseBrief ? [("OtherTests.Row", "Passed")] : [("AlphaTests.Check", "Passed"), ("BetaTests.Row", "Passed")]);
            if (reviseBrief)
                Assert.NotEqual(Assert.Single(goal.GateTestObligations).BriefVersion, goal.AuthoritativeBrief.Version);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, mainAfter);
            Assert.Equal(result.MergeCommitSha, mainAfter);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }
}
