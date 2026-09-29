using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceCohortFailureRouting : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsParallelAcceptanceCohortFailureRouting(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void RecordedCohortFailureRoutesBeforeParallelSoloAdmission()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Fix attributed acceptance failure");
        const string candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string check = "Tests.AttributedFailure";
        kernel.RecordAcceptanceFailure(goal.Id, [check], candidate, main,
            [new AcceptanceCheckAttribution(check, AcceptanceFailureOrigin.Introduced,
                "cohort-attribution=cohort-1 cohort-partition=partition-1 reproduced=Tests.AttributedFailure")]);
        Assert.Equal(GoalStatus.Verified, goal.Status);

        var acceptanceRuns = 0;
        var routingWrites = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            writeEscalation: (_, state, _) =>
            {
                Assert.Equal(GoalLifecycleState.AcceptanceFailed, state);
                routingWrites++;
            });
        driver.CohortAttributionRevisionReader = _ =>
            new GateReadyCandidateRevisionPair(candidate, main);

        new ConductorBatchLoop().Run(kernel, driver,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 },
            NoStopPath(), maxIterations: 1);

        Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetCapacityReservingAttempts(
            [goal.Id.Value]));
        Assert.Equal(0, acceptanceRuns);
        Assert.Equal(1, routingWrites);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
    }
}
