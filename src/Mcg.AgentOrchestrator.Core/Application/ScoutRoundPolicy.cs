namespace Mcg.AgentOrchestrator.Core;

public static class ScoutRoundPolicy
{
    public static bool IsScoutPlanner(Goal goal, TaskSpec task)
        => IsScoutPlannerForOrderedTasks(goal.Tasks, task);

    public static bool IsScoutPlannerForOrderedTasks(IReadOnlyList<TaskSpec> tasks, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Planner)
        {
            return false;
        }

        var taskIndex = tasks.ToList().FindIndex(candidate => candidate.Id == task.Id);
        return taskIndex >= 0 &&
            !tasks.Take(taskIndex).Any(candidate => candidate.RequiredRole == AgentRole.Researcher);
    }
}
