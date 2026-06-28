using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DispatchRecoveryView
{
    public static DispatchRecoveryDecision? Evaluate(Goal goal, TaskSpec task)
    {
        if (task.LastProcess is not { CompletedAt: null } process || task.LastVerification is not null)
        {
            return null;
        }

        return new DispatchRecoveryPolicy().Evaluate(
            process,
            IsProcessAlive(process.ProcessId),
            DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task));
    }

    public static DispatchRecoveryDecision? Evaluate(Goal goal, NextActionItem item)
    {
        if (item.TaskId is null || item.Kind != NextActionKind.RefreshRunningProcess)
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(task => task.Id == item.TaskId);
        return task is null ? null : Evaluate(goal, task);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
