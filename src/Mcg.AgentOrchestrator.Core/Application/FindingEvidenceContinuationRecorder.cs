namespace Mcg.AgentOrchestrator.Core;

public static class FindingEvidenceContinuationRecorder
{
    public static void RecordFindingEvidenceReviewContinuation(this AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        FindingEvidenceReviewContinuation continuation)
    {
        var goal = kernel.GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var prior = task.VerificationHistory.LastOrDefault();
        if (task.RequiredRole != AgentRole.Reviewer || prior is null ||
            !continuation.AppliesTo(goal, task, prior, continuation.CandidateIdentity))
            throw new InvalidOperationException("Finding evidence continuation does not bind the current Reviewer decision.");
        task.RecordFindingEvidenceReviewContinuation(continuation);
    }
}
