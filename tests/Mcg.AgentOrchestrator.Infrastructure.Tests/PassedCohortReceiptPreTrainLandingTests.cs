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
            var firstCandidate = CreateWorktreeCandidate(repo, first.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/PrelandFirst.cs", "first");
            var secondCandidate = CreateWorktreeCandidate(repo, second.Id,
                "tests/PrelandSecond.cs", "second");
            foreach (var (goal, candidate) in new[] { (first, firstCandidate), (second, secondCandidate) })
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                    CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var trx = WritePassingTrx(repo, "preland-green.trx");
            var verifier = new BlockingAcceptanceVerifier(gateStarted, gateRelease,
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("preland", true, 0, null)],
                    TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: cleanup.Hooks);
            var pairCandidates = new[] { first, second }.Select(goal =>
                new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
            var pairSelection = ConductorAcceptanceCohortSelector.Select(pairCandidates).Selection;
            Assert.NotNull(pairSelection);
            var started = driver.RunAcceptanceCohort(pairSelection,
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
            var passedReceipt = Assert.Single(cohortStore.ReadPassedReceiptsForGoal(first.Id));

            var third = CreateCompletedGoal(kernel, "Third ready goal", repo);
            var thirdCandidate = CreateWorktreeCandidate(repo, third.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/PrelandThird.cs", "third");
            kernel.RecordGoalRefinement(third.Id, new RefinedSpec(third.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
            });
            kernel.MapCriterionEvidenceOwner(third.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: thirdCandidate);
            var currentCandidates = new[] { first, second, third }.Select(goal =>
                new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive))).ToArray();
            Assert.True(driver.FindLandablePassedCohortSelections(currentCandidates).Any(selection =>
                    selection.Members.Select(member => member.GoalId).SequenceEqual(
                        pairSelection.Members.Select(member => member.GoalId))),
                "The completed pair was discarded before the pre-landing pass despite its current passed receipt.");
            var landings = new List<ConductorLandingReceipt>();
            driver.SuccessfulLandingSink = landings.Add;
            var logPath = Path.Combine(workspace.OrchestratorDirectory, "logs", "preland.jsonl");
            BatchTickSummary? observedTick = null;
            _ = new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel, driver, ConductorAutonomyPolicy.Permissive,
                Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1,
                onTick: summary => observedTick = summary);

            var progress = string.Join(" | ", observedTick?.ProgressLines ?? []);
            Assert.Contains($"prelanded=true outcome=passed receipt={passedReceipt.ReceiptId}", progress);
            Assert.True(first.Status == GoalStatus.Completed,
                $"Expected first cohort member to land; status={first.Status}; progress={progress}");
            Assert.True(second.Status == GoalStatus.Completed,
                $"Expected second cohort member to land; status={second.Status}; progress={progress}");
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
