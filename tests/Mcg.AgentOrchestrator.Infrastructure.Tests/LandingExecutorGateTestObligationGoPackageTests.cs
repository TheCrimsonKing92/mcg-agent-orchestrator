using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Landing fixtures share the existing global git-runner isolation and owned temporary roots.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorGateTestObligationGoPackageTests
{
    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    public void AppliedResolution_PrintsSelectorPackageAndSource(string selectedOutcome)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "a/run_test.go");
            var worktree = GoalWorktrees.Ensure(repo, originalGoal.Id);
            TestClassRenameEvidenceTests.Write(worktree, "a/run_test.go", "package a\n");
            TestClassRenameEvidenceTests.Commit(worktree, "Prepare Go package source", "a/run_test.go");
            var candidate = LandingExecutorTests.ReadGit(worktree, "rev-parse", "HEAD");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["TestRun"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var receipt = GateTestObligationGoPackageResolutionTests.WriteGoReceipt(worktree);
            GateTestObligationGoPackageResolutionTests.ChangeMethod(receipt, "example.com/m/a", resultOutcome: selectedOutcome);
            var (source, _, _) = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, [], additionalPaths: [receipt]);
            Assert.Equal(selectedOutcome == "Passed" ? GateTestObligationEvaluator.Outcome.SelectorNotFound : GateTestObligationEvaluator.Outcome.Failed,
                Assert.Single(GateTestObligationEvaluator.Evaluate(goal, workspace.OrchestratorDirectory, candidate)).Outcome);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var result = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, candidate);
                if (selectedOutcome == "Passed") Assert.Empty(result.ObligationLines);
                else Assert.Contains("failed: TestRun", Assert.Single(result.ObligationLines), StringComparison.Ordinal);
            });

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("GATE_TEST_OBLIGATION_PACKAGE_RESOLVED", StringComparison.Ordinal)).ToArray();
            Assert.Equal($"GATE_TEST_OBLIGATION_PACKAGE_RESOLVED id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
                $"selector=TestRun package=example.com/m/a directory=a source={source}", Assert.Single(lines));
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("ExistingTests")]
    [InlineData("TestRun")]
    public void NoResolution_PrintsNoPackageProvenance(string selector)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/change.txt");
            var worktree = GoalWorktrees.Ensure(repo, originalGoal.Id);
            var candidate = LandingExecutorTests.ReadGit(worktree, "rev-parse", "HEAD");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, [selector]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var receipt = GateTestObligationGoPackageResolutionTests.WriteGoReceipt(worktree);
            ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory, goal, candidate,
                [("ExistingTests.Check", "Passed")], additionalPaths: [receipt]);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var result = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, candidate);
                if (selector == "ExistingTests") Assert.Empty(result.ObligationLines);
                else Assert.Contains("selector-not-found", Assert.Single(result.ObligationLines), StringComparison.Ordinal);
            });

            Assert.DoesNotContain("GATE_TEST_OBLIGATION_PACKAGE_RESOLVED", output, StringComparison.Ordinal);
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }
}
