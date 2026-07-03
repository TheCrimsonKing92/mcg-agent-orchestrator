using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DispatchRecoveryView
{
    public static DispatchRecoveryDecision? Evaluate(Goal goal, TaskSpec task)
    {
        return EvaluateState(goal, task)?.RecoveryDecision;
    }

    public static DispatchAuthoritativeState? EvaluateState(Goal goal, TaskSpec task)
    {
        if (task.LastProcess is not { CompletedAt: null } process || task.LastVerification is not null)
        {
            return null;
        }

        return new DispatchStateSurface().Evaluate(goal.Id, task);
    }

    public static DispatchRecoveryDecision? Evaluate(Goal goal, NextActionItem item)
    {
        return EvaluateState(goal, item)?.RecoveryDecision;
    }

    public static DispatchAuthoritativeState? EvaluateState(Goal goal, NextActionItem item)
    {
        if (item.TaskId is null || item.Kind != NextActionKind.RefreshRunningProcess)
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(task => task.Id == item.TaskId);
        return task is null ? null : EvaluateState(goal, task);
    }
}
