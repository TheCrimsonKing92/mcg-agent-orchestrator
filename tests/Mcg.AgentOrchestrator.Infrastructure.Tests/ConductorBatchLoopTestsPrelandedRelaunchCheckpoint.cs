using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPrelandedRelaunchCheckpoint : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void PassedCohortReceipt_EvictedCompletedMembersAreSavedOnceBeforeRelaunch() =>
        AssertPrelandedRelaunch(mergeTrain: false);

    [Fact]
    public void PassedMergeTrainReceipt_EvictedCompletedMembersAreSavedOnceBeforeRelaunch() =>
        AssertPrelandedRelaunch(mergeTrain: true);

    private static void AssertPrelandedRelaunch(bool mergeTrain)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, mergeTrain ? 3 : 2)
                .Select(index => CreateCompletedGoal(kernel, $"Prelanded member {index}", repo)).ToArray();
            var paths = new[]
            {
                "src/Mcg.AgentOrchestrator.Infrastructure/PrelandFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PrelandSecond.cs",
                "tests/Mcg.AgentOrchestrator.Core.Tests/PrelandThird.cs"
            };
            foreach (var (goal, index) in goals.Select((goal, index) => (goal, index)))
            {
                var candidate = CreateWorktreeCandidate(repo, goal.Id, paths[index], $"member {index}");
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                {
                    AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
                });
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                    CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("preland", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(repo, "preland-relaunch-green.trx")]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true,
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks);
            new BackgroundGateStartHarness().Inline(driver);
            if (mergeTrain)
            {
                var started = driver.RunMergeTrain(ProjectTrainSelection(driver, goals), goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
                var store = new MergeTrainAcceptanceStore(
                    Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
            }
            else
            {
                var started = driver.RunAcceptanceCohort(ProjectSelection(driver, goals[0], goals[1]), goals,
                    ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
                Assert.Contains("outcome=inflight", started.Detail, StringComparison.Ordinal);
                var store = new CohortAcceptanceStore(
                    Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                Assert.Single(store.ReadPassedReceiptsForGoal(goals[0].Id));
            }

            var saved = new List<(GoalId GoalId, GoalStatus Status)>();
            var relaunchCalls = 0;
            var exception = Record.Exception(() => new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    var handoff = new ConductorLoopHandoffResult(
                        true, null, null, null, "fixture handoff observed");
                    return new ConductorSelfRelaunchResult(true, null, handoff.Reason, handoff);
                },
                selfRelaunchEnabled: true).Run(
                    kernel, driver, ConductorAutonomyPolicy.Permissive,
                    Path.Combine(repo, "stop-does-not-exist"), maxIterations: 1,
                    checkpointGoalTick: (checkpoint, requested) =>
                    {
                        var present = requested.Where(id => checkpoint.Goals.Any(goal => goal.Id == id)).ToArray();
                        var outcomes = new List<GoalSnapshotCheckpointResult>();
                        foreach (var id in present)
                        {
                            var status = checkpoint.GetGoal(id).Status;
                            saved.Add((id, status));
                            outcomes.Add(new GoalSnapshotCheckpointResult(id.Value,
                                GoalSnapshotCheckpointDisposition.Durable, null,
                                "state", "C:/fixture/state.db", "fixture"));
                        }
                        checkpoint.EvictTerminalGoalAggregates(present.Where(id =>
                            checkpoint.GetGoal(id).Status == GoalStatus.Completed));
                        return outcomes;
                    }));

            Assert.Null(exception);
            Assert.Equal(1, relaunchCalls);
            foreach (var goal in goals)
            {
                var persisted = Assert.Single(saved.Where(entry => entry.GoalId == goal.Id));
                Assert.Equal(GoalStatus.Completed, persisted.Status);
                Assert.DoesNotContain(kernel.Goals, current => current.Id == goal.Id);
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
