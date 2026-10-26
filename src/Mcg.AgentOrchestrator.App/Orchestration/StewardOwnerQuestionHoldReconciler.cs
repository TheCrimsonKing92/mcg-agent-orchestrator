using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StewardOwnerQuestionHoldReconciler
{
    internal static void Reconcile(AgentOrchestratorKernel kernel, ConductEventLogWriter conduct,
        string? onlyGoalId, ISet<GoalId> changed)
    {
        foreach (var goal in kernel.Goals.ToArray())
        {
            if ((onlyGoalId is not null && goal.Id.Value != onlyGoalId) ||
                goal.IsTerminal || goal.Status == GoalStatus.Parked) continue;
            if (OwnerQuestionHoldBlocker.TryRead(goal) is not
                { State: "steward-owner-question", TriggerIdentity: not null, TaskId: not null } hold) continue;
            var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == hold.TaskId);
            if (task is null || task.Status == WorkTaskStatus.Failed ||
                task.LatestRetryAt is not { } retriedAt || retriedAt <= hold.StartedAt) continue;

            kernel.ClearGoalHold(goal.Id);
            changed.Add(goal.Id);
            conduct.Append("owner-question-hold-reconciled", goal.Id.Value,
                $"item=StewardTrigger:{hold.TriggerIdentity} target=retried " +
                $"retried_at={retriedAt.ToString("O", CultureInfo.InvariantCulture)} " +
                $"hold_started={hold.StartedAt.ToString("O", CultureInfo.InvariantCulture)}");
        }
    }
}
