using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public static class DispatchRecoveryView
{
    public static DispatchRecoveryDecision? Evaluate(Goal goal, TaskSpec task)
    {
        return EvaluateState(goal, task, commandLineSnapshot: null)?.RecoveryDecision;
    }

    public static DispatchRecoveryDecision? Evaluate(
        Goal goal,
        TaskSpec task,
        ProcessCommandLineSnapshot commandLineSnapshot)
    {
        return EvaluateState(goal, task, commandLineSnapshot)?.RecoveryDecision;
    }

    public static DispatchAuthoritativeState? EvaluateState(Goal goal, TaskSpec task)
    {
        return EvaluateState(goal, task, commandLineSnapshot: null);
    }

    public static DispatchAuthoritativeState? EvaluateState(
        Goal goal,
        TaskSpec task,
        ProcessCommandLineSnapshot? commandLineSnapshot)
    {
        if (task.LastProcess is not { CompletedAt: null } process || task.LastVerification is not null)
        {
            return null;
        }

        return new DispatchStateSurface().Evaluate(goal.Id, task, commandLineSnapshot);
    }

    public static DispatchRecoveryDecision? Evaluate(Goal goal, NextActionItem item)
    {
        return EvaluateState(goal, item)?.RecoveryDecision;
    }

    public static DispatchAuthoritativeState? EvaluateState(Goal goal, NextActionItem item)
    {
        return EvaluateState(goal, item, commandLineSnapshot: null);
    }

    public static DispatchAuthoritativeState? EvaluateState(
        Goal goal,
        NextActionItem item,
        ProcessCommandLineSnapshot? commandLineSnapshot)
    {
        if (item.TaskId is null || item.Kind != NextActionKind.RefreshRunningProcess)
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(task => task.Id == item.TaskId);
        return task is null ? null : EvaluateState(goal, task, commandLineSnapshot);
    }
}
