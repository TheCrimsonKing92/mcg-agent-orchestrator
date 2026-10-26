using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real Git integration: uses isolated repositories and the existing landing Git collection.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorGateTestObligationAnsweredRebindTests
{
    [Theory]
    [InlineData(true, "NewNameTests.Check", true)]
    [InlineData(false, "NewNameTests.Check", false)]
    [InlineData(true, "OtherWorkTests.Check", false)]
    public void Landing_AnswerAndPassingReplacement_ControlObligationHold(
        bool answered, string row, bool shouldLand)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/obligation.txt");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["OldNameTests"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            if (answered)
            {
                var request = kernel.RequestHumanInput(goal.Id, null, "Rename question");
                kernel.SubmitHumanInput(request.Id, "- OldNameTests becomes NewNameTests");
            }
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate, [(row, "Passed")]);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.Equal(shouldLand, result.MainAdvanced);
            var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            if (shouldLand)
            {
                Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
                Assert.NotEqual(mainBefore, mainAfter);
                Assert.Equal(result.MergeCommitSha, mainAfter);
                Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            }
            else
            {
                Assert.Equal(mainBefore, mainAfter);
                var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
                Assert.Equal(2, escalation.Decision!.Rung);
                Assert.Contains(Assert.Single(goal.GateTestObligations).Id[..8], escalation.Reason, StringComparison.Ordinal);
                Assert.Contains("selector-not-found", escalation.Reason, StringComparison.Ordinal);
                Assert.Contains("OldNameTests", escalation.Reason, StringComparison.Ordinal);
            }
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }
}
