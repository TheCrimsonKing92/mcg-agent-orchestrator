using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StoppedGoalTaskClosure
{
    internal static TaskSpec[] SelectClosableTasks(Goal goal) => goal.Tasks
        .Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
        .Where(task => task.LastProcess is not { IsRunning: true } process ||
                       !process.TrackedProcessIds.Any(IsProcessAlive))
        .ToArray();

    internal static ProgressEvent CloseOpenTasks(AgentOrchestratorKernel kernel, Goal stoppedGoal)
    {
        var stopEvent = stoppedGoal.Timeline[^1];
        var tasks = SelectClosableTasks(stoppedGoal);
        if (tasks.Length == 0)
            return stopEvent;

        kernel.CompleteTerminalGoalHumanInputRequests(stoppedGoal.Id, tasks.Select(task => task.Id).ToHashSet(),
            $"goal stop: goal is {stoppedGoal.Status}; human input closed with its task.");
        foreach (var task in tasks.Reverse())
        {
            kernel.ReportTaskProgress(stoppedGoal.Id, task.Id, WorkTaskStatus.Cancelled,
                $"goal stop: goal is {stoppedGoal.Status}; cancelled open task with no live tracked process.");
        }

        kernel.RecordGoalPolicyDecision(stoppedGoal.Id,
            $"goal stop: goal is {stoppedGoal.Status}; cancelled {tasks.Length} open task(s) with no live tracked process.");
        return stopEvent;
    }

    internal static GoalStateSnapshot ExportStoppedGoalState(
        AgentOrchestratorKernel kernel, GoalId goalId, GoalStateSnapshot stored)
    {
        var goal = kernel.GetGoal(goalId);
        if (goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded)
        {
            var cancelledTaskIds = goal.Tasks.Where(task => task.Status == WorkTaskStatus.Cancelled)
                .Select(task => task.Id).ToHashSet();
            var requests = stored.HumanInputRequests
                .Where(request => request.GoalId == goalId.Value && !request.IsCompleted &&
                                  request.TaskId is { } taskId && cancelledTaskIds.Contains(new TaskId(taskId)))
                .ToArray();
            kernel.IngestNewGoals(new OrchestratorSnapshot([], requests));
            kernel.CompleteTerminalGoalHumanInputRequests(goalId, cancelledTaskIds,
                $"goal stop: goal is {goal.Status}; human input closed with its task.");
        }

        var snapshot = kernel.ExportSnapshot();
        return new GoalStateSnapshot(snapshot.Goals.Single(item => item.Id == goalId.Value),
            goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded
                ? snapshot.HumanInputRequests.Where(request => request.GoalId == goalId.Value).ToArray()
                : []);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
        {
            return false;
        }
    }
}
