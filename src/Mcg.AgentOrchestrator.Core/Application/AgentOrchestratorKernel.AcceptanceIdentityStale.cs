namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public int RecordAcceptanceIdentityStale(GoalId goalId, string attemptId) =>
        GetGoal(goalId).RecordAcceptanceIdentityStale(attemptId);

    public void ResetAcceptanceIdentityStale(GoalId goalId) =>
        GetGoal(goalId).ResetAcceptanceIdentityStale();
}
