using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FailedGoalRetryApplicationExecutor
{
    internal static string? Apply(
        GoalId goalId,
        TaskId targetTaskId,
        FailedGoalRecoveryDecision decision,
        Func<IReadOnlyList<string>> buildCriterionFeedback,
        Action<GoalId, TaskId, string> recordTaskNote,
        Func<GoalId, TaskId, IReadOnlyList<string>, int> recordCriterionRetryFeedback,
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec> retryTask)
    {
        if (!string.IsNullOrWhiteSpace(decision.WarningMessage))
            recordTaskNote(goalId, targetTaskId, decision.WarningMessage);
        if (decision.Action == FailedGoalRecoveryAction.CriterionRetry)
            recordCriterionRetryFeedback(goalId, targetTaskId, buildCriterionFeedback());

        try
        {
            retryTask(
                goalId,
                targetTaskId,
                decision.Reason,
                decision.RoundKind,
                decision.RetryCause ?? throw new InvalidOperationException(
                    $"Recovery action {decision.Action} must carry a typed retry cause."));
        }
        catch (InvalidOperationException ex) when (
            decision.Reason.StartsWith("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal) &&
            ex.Message.Contains("running process", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message;
        }

        return null;
    }
}
