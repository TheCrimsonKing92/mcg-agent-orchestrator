namespace Mcg.AgentOrchestrator.Core;

public static class TaskDisplayNumber
{
    public static int Resolve(Goal goal, TaskId taskId)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == taskId)
            {
                return index + 1;
            }
        }

        throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }
}
