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

    [Fact]
    public void CommittedTargetRequirementRejectsRoleOnlyFallback()
    {
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(Facts(AgentRole.Developer) with
        {
            ExplicitTargetTaskId = null,
            RequiresCommittedTarget = true
        });

        Assert.Equal(FailedGoalVerifyingFindingRouteKind.TargetUnavailable, route.Kind);
        Assert.Null(route.TargetTaskId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedTargetRetainsRetryBounds(bool lifetime)
    {
        var facts = Facts(AgentRole.Developer) with
        {
            RequiresCommittedTarget = true,
            Round = lifetime ? 1 : 3,
            LifetimeRound = lifetime ? 6 : 0,
            LifetimeBackstop = 6
        };

        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(facts);

        Assert.Equal(lifetime ? FailedGoalVerifyingFindingRouteKind.LifetimeBackstopReached
            : FailedGoalVerifyingFindingRouteKind.RetryCapReached, route.Kind);
        Assert.Equal(facts.ExplicitTargetTaskId, route.TargetTaskId);
    }

    [Fact]
    public void CommittedTargetDoesNotOverrideOperatorEvidenceAuthority()
    {
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(Facts(AgentRole.Developer) with
        {
            RequiresCommittedTarget = true,
            ReviewerEscalatesToOperator = true
        });

        Assert.Equal(FailedGoalVerifyingFindingRouteKind.OperatorEvidenceRequired, route.Kind);
    }

    [Fact]
    public void CommittedTargetDoesNotBypassRepeatedFailureConvergenceHold()
    {
        var route = FailedGoalRecoveryPolicy.SelectVerifyingFindingRoute(Facts(AgentRole.Developer) with
        {
            RequiresCommittedTarget = true,
            RepeatedFailure = new PreReviewRepeatedFailureSummary(3, ["repeated-test"])
        });

        Assert.Equal(FailedGoalVerifyingFindingRouteKind.RepeatedFailingTestSet, route.Kind);
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
