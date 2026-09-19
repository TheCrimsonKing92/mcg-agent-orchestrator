using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal delegate ProcessBatchExecutionResult? GoalDispatchRefresh(AgentOrchestratorKernel kernel, Goal goal);

internal static class DetachedDispatchHoldReasonBuilder
{
    internal static string? Build(Goal goal, ProcessBatchExecutionResult? refreshResult)
    {
        if (refreshResult?.RefreshOutcomes is not { } outcomes)
        {
            return null;
        }

        for (var index = 0; index < Math.Min(refreshResult.Tasks.Count, outcomes.Count); index++)
        {
            var refreshedTask = refreshResult.Tasks[index];
            var outcome = outcomes[index];
            if (goal.Tasks.SingleOrDefault(task => task.Id == refreshedTask.Id)?.Status != WorkTaskStatus.Running ||
                !outcome.ProcessRecord.WasGracefullyDetachedByConductor ||
                outcome.RecoveryDecision is not
                    { Action: DispatchRecoveryAction.Hold, Blocker: not null } decision ||
                !decision.Blocker.StartsWith("exit-artifact-", StringComparison.Ordinal))
            {
                continue;
            }

            return $"Detached worker process pid={outcome.ProcessRecord.ProcessId} cannot be reconciled: " +
                   $"{decision.Reason}; artifact={decision.EvidencePath}";
        }

        return null;
    }
}
