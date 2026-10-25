using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Evidence = ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence;

// Real Git integration: uses the same isolated fixture and collection as other landing tests.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorGateTestObligationSelectorFormTests
{
    [Fact]
    public void Execute_NestedAndBareSelectors_AdvancesMain() => VerifyLanding(missingNested: false);

    [Fact]
    public void Execute_UnrelatedNestedSuffix_EscalatesBeforeMutation() => VerifyLanding(missingNested: true);

    private static void VerifyLanding(bool missingNested)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) =
                LandingExecutorTests.CreateAcceptedCandidate(repo, "src/obligation.txt");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal,
                [Evidence.NestedSelector, Evidence.GoSelector]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            var paths = Evidence.WriteSelectorReceipts(Path.Combine(workspace.OrchestratorDirectory, "selector-evidence"),
                missingNested ? "XWorkerDispatchPlannerHandoffTests" : Evidence.NestedClass);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                [], additionalPaths: paths);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            if (missingNested)
            {
                Assert.False(result.MainAdvanced);
                Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
                var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
                Assert.Equal(2, escalation.Decision!.Rung);
                Assert.Contains("selector-not-found", escalation.Reason, StringComparison.Ordinal);
                Assert.Contains(Evidence.NestedSelector, escalation.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain(Evidence.GoSelector, escalation.Reason, StringComparison.Ordinal);
            }
            else
            {
                Assert.True(result.MainAdvanced, result.Message);
                Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
                var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
                Assert.NotEqual(mainBefore, mainAfter);
                Assert.Equal(result.MergeCommitSha, mainAfter);
                Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            }
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }
}
