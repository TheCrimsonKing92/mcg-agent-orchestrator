using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalWalkOrderTests
{
    [Xunit.Fact]
    public void CriticalPrecedesOlderNormalGoalsWithoutReorderingTies()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { kernel.CreateGoal("A"), kernel.CreateGoal("B"), kernel.CreateGoal("C"), kernel.CreateGoal("D") };
        kernel.SetGoalPriority(goals[3].Id, GoalPriority.Critical, "test", "setup");
        Assert.Equal(new[] { goals[3], goals[0], goals[1], goals[2] }, GoalWalkOrder.Order(goals));
    }

    [Xunit.Fact]
    public void HighThenNormalThenLow()
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { kernel.CreateGoal("A"), kernel.CreateGoal("B"), kernel.CreateGoal("C") };
        kernel.SetGoalPriority(goals[0].Id, GoalPriority.Low, "test", "setup");
        kernel.SetGoalPriority(goals[1].Id, GoalPriority.High, "test", "setup");
        Assert.Equal(new[] { goals[1], goals[2], goals[0] }, GoalWalkOrder.Order(goals));
    }
}
