namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static void ReplaceEarlierRoundCriterionRetryFeedback(TaskSpec task, DateTimeOffset? previousRetryAt, string retryMessage)
    {
        if (task.CriterionRetryFeedback.Count == 0 || task.CriterionRetryFeedbackRoundAt == previousRetryAt)
            return;

        task.RecordCriterionRetryFeedback([retryMessage], previousRetryAt);
    }
}
