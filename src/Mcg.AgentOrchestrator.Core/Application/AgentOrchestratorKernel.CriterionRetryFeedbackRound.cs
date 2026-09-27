namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static void ReplaceEarlierRoundCriterionRetryFeedback(
        TaskSpec task, DateTimeOffset? previousRetryAt, DateTimeOffset retryAt, string retryMessage)
    {
        // Retry admission can replay an already-applied retry at its original marker.
        if (previousRetryAt == retryAt || task.CriterionRetryFeedback.Count == 0 ||
            task.CriterionRetryFeedbackRoundAt == previousRetryAt)
            return;

        task.RecordCriterionRetryFeedback([retryMessage], previousRetryAt);
    }
}
