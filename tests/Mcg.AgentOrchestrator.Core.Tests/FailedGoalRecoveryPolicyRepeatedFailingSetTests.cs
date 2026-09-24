using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

public sealed class FailedGoalRecoveryPolicyRepeatedFailingSetTests
{
    [Theory]
    [InlineData(AgentRole.Developer, FailedGoalVerifyingFindingRouteKind.RepeatedFailingTestSet)]
    [InlineData(AgentRole.Tester, FailedGoalVerifyingFindingRouteKind.Routed)]
    public void RepeatedSetHoldsOnlyDeveloperLeg(
        AgentRole targetRole, FailedGoalVerifyingFindingRouteKind expected)
    {
        var facts = new FailedGoalVerifyingFindingRouteFacts(
            new TaskId("tester-trigger"), AgentRole.Tester, "attempt", new TaskId("developer-target"),
            false, targetRole, false, 1, 7, 5, false, RetryCause.NewTestFinding,
            [new FailedGoalFindingRouteTask(new TaskId("developer-target"), AgentRole.Developer)],
            new PreReviewRepeatedFailureSummary(3, ["Example.A"]));

        Assert.Equal(expected, FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(facts).Kind);
    }
}
