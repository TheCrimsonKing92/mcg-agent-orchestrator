using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private void ReapGoalOnce(
        AgentOrchestratorKernel kernel, Goal goal, HashSet<string> reapedGoals,
        int tick, List<string>? tickLines)
    {
        if (!reapedGoals.Add(goal.Id.Value))
        {
            return;
        }

        try
        {
            _reapGoalRunningDispatches(kernel, goal);
        }
        catch (Exception exception) when (exception is not DispatchRecordWriteException { IsFatal: true })
        {
            // A failed reap remains eligible for retry; this diagnostic is advisory only.
            reapedGoals.Remove(goal.Id.Value);
            var fault = $"REAP_FAULT tick={tick} goal={ShortGoalId(goal.Id.Value)} exception={Sanitize(exception.GetType().Name)} message={SanitizeReason(exception.Message)}";
            EmitProgress(fault, tickLines);
            kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {tick}: {fault}");
        }
    }
}
