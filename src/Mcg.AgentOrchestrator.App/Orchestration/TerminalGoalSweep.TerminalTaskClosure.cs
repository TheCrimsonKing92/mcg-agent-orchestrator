using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class TerminalGoalSweep
{
    private static void CloseStaleTasksOnTerminalGoal(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string prefix,
        string desyncEvidence,
        List<TerminalGoalSweepRepair> repairs)
    {
        var staleTasks = goal.Tasks
            .Where(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
            .Select(task => (task.Id, task.Status))
            .ToArray();
        if (staleTasks.Length == 0)
            return;

        foreach (var (taskId, _) in staleTasks.Reverse())
        {
            kernel.ReportTaskProgress(
                goal.Id,
                taskId,
                WorkTaskStatus.Cancelled,
                $"terminal stale-goal sweep: goal is {goal.Status}; cancelled stale non-terminal task.");
        }

        kernel.RecordGoalPolicyDecision(
            goal.Id,
            $"terminal stale-goal sweep: goal is {goal.Status}; cancelled {staleTasks.Length} stale non-terminal task(s); goal status unchanged.");
        repairs.Add(new TerminalGoalSweepRepair(
            "terminal-task-desync",
            $"{desyncEvidence}; action=cancel-stale-tasks; cancelledTasks={string.Join(",", staleTasks.Select(task => $"{task.Id.Value[..8]}:{task.Status}"))}",
            $"conduct {prefix} --loop"));
    }
}
