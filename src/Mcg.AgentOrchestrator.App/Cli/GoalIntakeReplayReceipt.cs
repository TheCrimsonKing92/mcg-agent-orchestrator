using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalIntakeReplayReceipt
{
    public static void Print(
        GoalIntakeRequestRecord record,
        string requestKey,
        IOrchestratorStateRepository stateRepository,
        string portfolioPath,
        bool summary,
        ref Goal? currentGoal)
    {
        if (!summary) GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(record);
        if (record.State == GoalIntakeRequestStates.Created)
        {
            var replayKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            currentGoal = replayKernel.Goals.FirstOrDefault(goal =>
                goal.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
            if (currentGoal is null)
            {
                throw new InvalidOperationException(
                    $"GOAL_INTAKE_RECEIPT_INVALID requestKey={requestKey} reason=created-goal-missing");
            }
            if (summary)
                GoalCreationSummaryView.Print(currentGoal, "replayed", portfolioPath);
            else
                GoalTextView.PrintGoal(currentGoal);
            return;
        }
        if (summary) GoalCreationSummaryView.PrintProgress(record);
        if (record.State == GoalIntakeRequestStates.Failed)
        {
            throw new InvalidOperationException(
                $"GOAL_INTAKE_REPLAY_FAILED requestKey={requestKey} " +
                $"failureCode={record.FailureCode} detail={record.FailureDetail}");
        }
    }
}
