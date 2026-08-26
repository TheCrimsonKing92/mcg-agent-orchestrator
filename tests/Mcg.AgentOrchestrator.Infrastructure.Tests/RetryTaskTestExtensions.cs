using Mcg.AgentOrchestrator.Core;

internal static class RetryTaskTestExtensions
{
    public static TaskSpec RetryTask(
        this AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string message,
        bool invalidateDownstream = true,
        RetryRoundKind? retryRoundKind = null)
        => kernel.RetryTask(
            goalId,
            taskId,
            message,
            RetryCause.ContractClarification,
            invalidateDownstream,
            retryRoundKind);
}
