namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public static bool IsCancelledReviewAwaitingRerun(Goal goal, TaskSpec task) =>
        task.RequiredRole == AgentRole.Reviewer &&
        task.Status == WorkTaskStatus.Cancelled &&
        !IsTerminalGoalStatus(goal.Status);

    public static string FormatCancelledReviewRerunGuidance(Goal goal, TaskSpec task)
    {
        var taskNumber = goal.Tasks
            .Select((candidate, index) => (candidate.Id, Number: index + 1))
            .Single(candidate => candidate.Id == task.Id).Number;
        return $"Reviewer task {task.Id.Value} (task {taskNumber}) of goal {goal.Id.Value} was cancelled; " +
            $"re-run the review with adjudicate --goal {goal.Id.Value} {taskNumber} route --cause <cause> " +
            $"or retry --goal {goal.Id.Value} {taskNumber} --cause <cause>.";
    }
}
