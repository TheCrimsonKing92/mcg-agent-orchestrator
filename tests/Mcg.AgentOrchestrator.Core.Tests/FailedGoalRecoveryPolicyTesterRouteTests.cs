using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Core;

public sealed class FailedGoalRecoveryPolicyTesterRouteTests
{
    [Fact]
    public void TesterTestEvidenceTargetsItsOwnTaskRatherThanDeveloper()
    {
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(Facts(AgentRole.Tester));

        Assert.Equal(FailedGoalVerifyingFindingRouteKind.Routed, route.Kind);
        Assert.Equal(AgentRole.Tester, route.TargetRole);
        Assert.Equal(new TaskId("tester-trigger"), route.TargetTaskId);
    }

    [Fact]
    public void TesterDeveloperFindingStillTargetsPriorDeveloper()
    {
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(Facts(AgentRole.Developer));

        Assert.Equal(FailedGoalVerifyingFindingRouteKind.Routed, route.Kind);
        Assert.Equal(AgentRole.Developer, route.TargetRole);
        Assert.Equal(new TaskId("developer-target"), route.TargetTaskId);
    }

    private static FailedGoalVerifyingFindingRouteFacts Facts(AgentRole projectedRole) =>
        new(new TaskId("tester-trigger"), AgentRole.Tester, "verification:1:none",
            ExplicitTargetTaskId: new TaskId("developer-target"), RequiresCommittedTarget: false,
            ReviewerTargetRole: projectedRole, ReviewerEscalatesToOperator: false,
            Round: 1, StopRound: 3, WarningRound: 2,
            MissingFindingResult: false, ObservedCause: RetryCause.NewTestFinding,
            PriorTasks: ImmutableArray.Create(
                new FailedGoalFindingRouteTask(new TaskId("developer-target"), AgentRole.Developer)));
}
