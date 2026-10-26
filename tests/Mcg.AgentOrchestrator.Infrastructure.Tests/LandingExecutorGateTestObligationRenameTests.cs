using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git landing integration; shares the existing LandingGitRunner collection's global-runner isolation.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorGateTestObligationRenameTests
{
    [Fact]
    public void ProvenGoalRename_LandsWithoutObligationWaiver()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/rename.txt");
            var candidate = PrepareCandidate(repo, originalGoal, unrelated: false);
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["OldNameTests"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                [("NewNameTests.Check", "Passed")]);
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound,
                Assert.Single(GateTestObligationEvaluator.Evaluate(goal, workspace.OrchestratorDirectory, candidate)).Outcome);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }

    [Fact]
    public void UnrelatedReplacement_HoldsMainAtRungTwo()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/unrelated.txt");
            var candidate = PrepareCandidate(repo, originalGoal, unrelated: true);
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["OldNameTests"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                [("OtherWorkTests.Check", "Passed")]);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.False(result.MainAdvanced);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(2, escalation.Decision!.Rung);
            Assert.Contains(Assert.Single(goal.GateTestObligations).Id[..8], escalation.Reason, StringComparison.Ordinal);
            Assert.Contains("selector-not-found", escalation.Reason, StringComparison.Ordinal);
            Assert.Contains("OldNameTests", escalation.Reason, StringComparison.Ordinal);
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }

    private static string PrepareCandidate(string repo, Goal goal, bool unrelated)
    {
        var worktree = GoalWorktrees.Ensure(repo, goal.Id);
        TestClassRenameEvidenceTests.AddOld(worktree);
        if (unrelated) TestClassRenameEvidenceTests.ReplaceWithUnrelated(worktree);
        else TestClassRenameEvidenceTests.Rename(worktree, TestClassRenameEvidenceTests.OldPath,
            TestClassRenameEvidenceTests.NewPath, "NewNameTests");
        var candidate = LandingExecutorTests.ReadGit(worktree, "rev-parse", "HEAD");
        GoalOperationJournal.AcceptancePassed(repo, goal, "conductor:acceptance", candidate,
            LandingExecutorTests.ReadGit(repo, "rev-parse", "main"), "passing acceptance for renamed candidate",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        return candidate;
    }
}
