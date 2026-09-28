using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingTickDurability : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingTickDurability(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void PrelandedCohortCarryIsSavedBeforeSelfRelaunchReload()
    {
        var fixture = GoalOwnedLinesPatchEquivalenceTests.CreateConflictFixture(changeGoalLine: false);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            // This first eligible goal has no parallel result, so the walk stops before
            // visiting either prelanded cohort member once relaunch is scheduled.
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Ordinary goal first");
            var target = CreateVerifiedSimpleGoal(kernel, "Cohort candidate A");
            var partner = CreateVerifiedSimpleGoal(kernel, "Other cohort member");
            kernel.RecordGoalRefinement(target.Id, new RefinedSpec(
                target.Objective, ["Full acceptance passes"], VerificationClass.TestVerifiable, [], []));
            kernel.MapCriterionEvidenceOwner(target.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "reviewer", CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: fixture.OldHead);
            var original = Assert.Single(target.CriterionEvidenceObligations);
            var persisted = kernel.ExportSnapshot();
            var relaunchCalls = 0;
            const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var paths = new Dictionary<GoalId, IReadOnlyList<string>>
            {
                [target.Id] = ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                [partner.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CohortPartnerTests.cs"]
            };
            var projector = new GateReadyCandidateProjector(
                goalId => new GateReadyCandidateRevisionPair(
                    goalId == target.Id ? fixture.OldHead : partner.Id.Value.PadRight(40, 'b')[..40],
                    mainRevision),
                goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            ConductorDriver? driver = null;
            driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                classifyRisk: _ => ChangeRiskTier.Behavior,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    Assert.Equal(2, selection.Members.Count);
                    Assert.Null(AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
                        target, fixture.NewHead, kernel, "cohort gate", fixture.Repository));
                    Assert.Equal(CriterionEvidenceState.Satisfied, original.State);
                    kernel.CompleteGoal(target.Id, "Cohort landing, recording, and cleanup completed.");
                    kernel.CompleteGoal(partner.Id, "Cohort landing, recording, and cleanup completed.");
                    driver!.SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                        target.Id.Value, paths[target.Id], fixture.NewHead));
                    driver.SuccessfulLandingSink?.Invoke(new ConductorLandingReceipt(
                        partner.Id.Value, paths[partner.Id], fixture.NewHead));
                    RunGit(fixture.Repository, "merge", "--ff-only", "candidate-old");
                    return new ConductorAcceptanceCohortRunResult(
                        null,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id))
                            .ToDictionary(goal => goal.Id.Value,
                                goal => new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8],
                                    policy.Name, new ConductorAdvanceOutcome.Executed(
                                        GoalLifecycleState.Verified, "cohort landed")),
                                StringComparer.Ordinal),
                        "outcome=passed");
                });

            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    var reloaded = AgentOrchestratorKernel.FromSnapshot(persisted);
                    var goal = reloaded.GetGoal(target.Id);
                    Assert.Equal(GoalStatus.Completed, goal.Status);
                    var obligation = Assert.Single(goal.CriterionEvidenceObligations);
                    Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
                    Assert.Equal(fixture.NewHead, obligation.ExpectedCandidateSha);
                    Assert.Equal(fixture.NewHead, obligation.CandidateSha);
                    Assert.Equal(GoalStatus.Completed, reloaded.GetGoal(partner.Id).Status);
                    return ConductorSelfRelaunchResult.PreparationFailed("fixture", "reload observed");
                },
                selfRelaunchEnabled: true).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 3,
                    persistGoalTick: (checkpoint, changedIds) =>
                    {
                        if (changedIds.Contains(target.Id)) persisted = checkpoint.ExportSnapshot();
                    });
            Assert.Equal(1, relaunchCalls);
        }
        finally
        {
            TryDeleteDirectory(fixture.Repository);
        }
    }
}
