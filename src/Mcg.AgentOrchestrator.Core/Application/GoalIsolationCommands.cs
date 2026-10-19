namespace Mcg.AgentOrchestrator.Core;

public static class GoalIsolationCommands
{
    public static void Apply(AgentOrchestratorKernel kernel, GoalId goalId, GoalIsolation isolation,
        string actor, string rationale)
    {
        var goal = kernel.GetGoal(goalId);
        var previous = goal.Isolation;
        goal.SetIsolation(isolation);
        kernel.RecordGoalPolicyDecision(goalId,
            $"Goal isolation set: previous={previous} new={isolation} actor={actor} reason={(string.IsNullOrWhiteSpace(rationale) ? "(no reason given)" : rationale)}");
    }
}
