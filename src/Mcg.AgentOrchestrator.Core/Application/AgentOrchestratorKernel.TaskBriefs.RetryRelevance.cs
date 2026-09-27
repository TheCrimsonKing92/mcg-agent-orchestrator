namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static List<ProgressEvent> SelectUpstreamRetryEvents(
        Goal goal,
        TaskSpec recipient,
        IReadOnlyList<ProgressEvent> retryEvents)
    {
        var recipientIndex = FindTaskIndex(goal, recipient.Id);
        return retryEvents
            .Where(evt => IsUpstreamRetryEvent(goal, recipient.Id, recipientIndex, evt))
            .ToList();
    }

    private static bool IsUpstreamRetryEvent(
        Goal goal,
        TaskId recipientId,
        int? recipientIndex,
        ProgressEvent evt)
    {
        if (evt.TaskId is not { } retriedTaskId)
        {
            return true;
        }

        if (retriedTaskId == recipientId)
        {
            return true;
        }

        var retriedTaskIndex = FindTaskIndex(goal, retriedTaskId);
        return recipientIndex is { } recipientPosition &&
               retriedTaskIndex is { } retriedPosition &&
               retriedPosition < recipientPosition;
    }

    private static int? FindTaskIndex(Goal goal, TaskId taskId)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == taskId)
            {
                return index;
            }
        }

        return null;
    }
}
