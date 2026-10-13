namespace Mcg.AgentOrchestrator.Core;

internal static class ReviewFindingDispatchContext
{
    internal static TaskVerificationRecord Attach(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester) ||
            !verification.WorkerResultPresent)
        {
            return verification;
        }

        return verification with
        {
            ReviewFindingTouchedAnchors = task.LastDispatch?.ReviewFindingTouchedAnchors ?? [],
            ReviewFindingTouchProofDiagnostic = task.LastDispatch?.ReviewFindingTouchProofDiagnostic,
            ReviewedCommit = task.LastDispatch?.BaseCommit
        };
    }
}
