using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AwaitingVerificationHoldReasonBuilder
{
    internal static string Build(Goal goal)
    {
        var worklist = AgentOrchestratorKernel.BuildVerificationWorklist(goal);
        if (worklist.Items.Count == 0)
        {
            return "All tasks done; awaiting task verification gates — auto-reconcile will advance goal to Verified";
        }

        var blocked = worklist.Items
            .Take(3)
            .Select(item =>
                $"task={item.TaskId.Value[..8]} role={item.Role} gate={item.GateStatus} detail={item.Message}");
        var remainder = worklist.Items.Count > 3
            ? $"; plus {worklist.Items.Count - 3} more unsatisfied task gate(s)"
            : string.Empty;
        return $"Task verification gates require operator attention: {string.Join("; ", blocked)}{remainder}";
    }
}
