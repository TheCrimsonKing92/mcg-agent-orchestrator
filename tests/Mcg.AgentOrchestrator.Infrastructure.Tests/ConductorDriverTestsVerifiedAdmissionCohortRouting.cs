using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static ConductorDriverTestsVerifiedAdmissionDecision;

public sealed class ConductorDriverTestsVerifiedAdmissionCohortRouting
{
    [Fact]
    public void RecordedCohortFailureKeepsRoutingEffectsAndCarriesAdmissionDecision()
    {
        var (kernel, goal) = SimpleGoal("Fix attributed acceptance failure");
        PassVerification(kernel, goal, goal.Tasks.Single());
        const string candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string check = "Tests.AttributedFailure";
        const string evidence = "cohort-attribution=cohort-1 cohort-partition=partition-1 reproduced=Tests.AttributedFailure";
        const string reason = "Acceptance verification failed; review and fix before landing. cohort-attribution=cohort-1 cohort-partition=partition-1 reproduced=Tests.AttributedFailure";
        kernel.RecordAcceptanceFailure(goal.Id, [check], candidate, main,
            [new AcceptanceCheckAttribution(check, AcceptanceFailureOrigin.Introduced, evidence)]);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        var acceptanceRuns = 0;
        var routingWrites = 0;
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            acceptanceRuns++;
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        }, writeEscalation: (routed, state, text) =>
        {
            Assert.Same(goal, routed);
            Assert.Equal(GoalStatus.AcceptanceFailed, routed.Status); // Kernel route precedes the escalation write.
            Assert.Equal(GoalLifecycleState.AcceptanceFailed, state);
            Assert.Equal(reason, text);
            routingWrites++;
        });
        driver.CohortAttributionRevisionReader = _ => new GateReadyCandidateRevisionPair(candidate, main);
        driver.BeginTick(kernel, 1);
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(GoalLifecycleState.AcceptanceFailed, escalated.State);
        Assert.Equal(reason, escalated.Reason);
        Assert.Equal(ConductorEscalationKind.AcceptanceVerificationFailed, escalated.Kind);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Equal(1, routingWrites);
        Assert.Equal(0, acceptanceRuns);
        Assert.All(goal.Tasks, task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
        Assert.Equal(candidate, goal.LatestAcceptanceFailure!.BranchHeadSha);
        Assert.Equal(main, goal.LatestAcceptanceFailure.MainHeadSha);
        var decision = AssertDecisionAndPayload(escalated, 2, "cohort-attribution-failure");
        AssertFact(decision, "goalStatus", "Verified");
        AssertFact(decision, "ownerReviewHold", "false");
        AssertFact(decision, "cohortAttribution", "true");
        AssertFact(decision, "cohortAttributionEvidence", evidence);
        AssertFact(decision, "apparatusHold", "");
    }
}
