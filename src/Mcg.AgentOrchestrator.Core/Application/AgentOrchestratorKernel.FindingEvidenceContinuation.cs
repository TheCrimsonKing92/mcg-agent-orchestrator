namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public void RecordFindingEvidenceReviewContinuation(GoalId goalId, TaskId taskId,
        FindingEvidenceReviewContinuation continuation)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var prior = task.VerificationHistory.LastOrDefault();
        if (task.RequiredRole != AgentRole.Reviewer || prior is null ||
            !continuation.AppliesTo(goal, task, prior, continuation.CandidateIdentity))
            throw new InvalidOperationException("Finding evidence continuation does not bind the current Reviewer decision.");
        task.RecordFindingEvidenceReviewContinuation(continuation);
    }
}
