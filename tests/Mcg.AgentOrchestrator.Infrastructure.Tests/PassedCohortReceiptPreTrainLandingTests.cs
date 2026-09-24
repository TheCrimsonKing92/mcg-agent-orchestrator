using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PassedCohortReceiptPreTrainLandingTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void CompletedPassedCohortReceipt_LandsPairBeforeMergeTrainSelection()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First passed cohort member", repo);
            var second = CreateCompletedGoal(kernel, "Second passed cohort member", repo);
            _ = CreateWorktreeCandidate(repo, first.Id,
                "src/Mcg.AgentOrchestrator.Core/PrelandFirst.cs", "first");
            _ = CreateWorktreeCandidate(repo, second.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PrelandSecond.cs", "second");
            var trx = WritePassingTrx(repo, "preland-green.trx");
            var verifier = new BlockingAcceptanceVerifier(gateStarted, gateRelease,
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("preland", true, 0, null)],
                    TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var started = driver.RunAcceptanceCohort(ProjectSelection(driver, first, second),
                [first, second], ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
            Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The pair's background gate did not start.");
            gateRelease.Set();
            var cohortStore = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.True(SpinWait.SpinUntil(() =>
                cohortStore.ReadPassedReceiptsForGoal(first.Id).Count == 1 &&
                driver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(15)), "The pair's passed receipt was not ready for landing.");

            var third = CreateCompletedGoal(kernel, "Third ready goal", repo);
            _ = CreateWorktreeCandidate(repo, third.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/PrelandThird.cs", "third");
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;
            var logPath = Path.Combine(workspace.OrchestratorDirectory, "logs", "preland.jsonl");
            _ = new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel, driver, ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1);

            Assert.Equal(GoalStatus.Completed, first.Status);
            Assert.Equal(GoalStatus.Completed, second.Status);
            Assert.Contains(landings, landing => landing.GoalId == first.Id.Value);
            Assert.Contains(landings, landing => landing.GoalId == second.Id.Value);
            Assert.DoesNotContain(File.ReadAllLines(logPath), line =>
                line.Contains("ACCEPTANCE_TRAIN", StringComparison.Ordinal) &&
                (line.Contains(first.Id.Value[..8], StringComparison.Ordinal) ||
                 line.Contains(second.Id.Value[..8], StringComparison.Ordinal)));
            Assert.Empty(new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"))
                .ReadPassedReceiptsForGoal(first.Id));
        }
        finally
        {
            gateRelease.Set();
            DeleteDirectory(repo);
        }
    }
}
