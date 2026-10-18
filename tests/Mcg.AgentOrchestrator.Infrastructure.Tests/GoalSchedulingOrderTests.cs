using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalSchedulingOrderTests
{
    [Fact]
    public void AcceptanceUsesPriorityThenVerifiedOrder()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 4).Select(i => kernel.CreateGoal($"Goal {i}")).ToArray();
        var snapshot = kernel.ExportSnapshot();
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select((goal, i) => goal with
            {
                Timeline = goal.Timeline.Select(evt => evt with
                {
                    Kind = ProgressKind.GoalPolicyDecision, Message = "Verified", OccurredAt = start.AddMinutes(i)
                }).ToArray()
            }).ToArray()
        });
        goals = goals.Select(goal => kernel.GetGoal(goal.Id)).ToArray();
        kernel.SetGoalPriority(goals[3].Id, GoalPriority.High, "test", "setup");
        Assert.Equal(new[] { goals[3], goals[0], goals[1], goals[2] }, GoalSchedulingOrder.ForAcceptance(goals));
        kernel.SetGoalPriority(goals[2].Id, GoalPriority.Critical, "test", "setup");
        Assert.Equal(new[] { goals[2], goals[3], goals[0], goals[1] }, GoalSchedulingOrder.ForAcceptance(goals));
        kernel.SetGoalPriority(goals[0].Id, GoalPriority.Low, "test", "setup");
        Assert.Equal(new[] { goals[2], goals[3], goals[1], goals[0] }, GoalSchedulingOrder.ForAcceptance(goals));
    }

    [Fact]
    public void StarvedWaiterJoinsCriticalBandAndWalkTiesStayStable()
    {
        var kernel = new AgentOrchestratorKernel();
        var normal = kernel.CreateGoal("Normal");
        var high = kernel.CreateGoal("High");
        kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
        for (var i = 0; i < 3; i++) GoalEffectivePriorityIndex.IncrementGateOvertakes(normal);
        Assert.Equal(new[] { normal, high }, GoalSchedulingOrder.ForAcceptance([high, normal]));
        var peer = kernel.CreateGoal("Peer");
        Assert.Equal(new[] { peer, normal }, GoalSchedulingOrder.ForWalk([peer, normal]));
        Assert.Equal(new[] { normal, peer }, GoalSchedulingOrder.ForWalk([normal, peer]));
    }

    [Fact]
    public void GroupsKeepHighHeadButMaySkipNormalOrLiveHead()
    {
        var kernel = new AgentOrchestratorKernel();
        var head = kernel.CreateGoal("Head");
        var member = kernel.CreateGoal("Member");
        Assert.True(GoalSchedulingOrder.GroupKeepsHead([member.Id.Value], [head, member], new HashSet<string>()));
        kernel.SetGoalPriority(head.Id, GoalPriority.High, "test", "setup");
        Assert.False(GoalSchedulingOrder.GroupKeepsHead([member.Id.Value], [head, member], new HashSet<string>()));
        Assert.True(GoalSchedulingOrder.GroupKeepsHead([head.Id.Value], [head, member], new HashSet<string>()));
        Assert.True(GoalSchedulingOrder.GroupKeepsHead([member.Id.Value], [head, member], new HashSet<string> { head.Id.Value }));
    }
}
