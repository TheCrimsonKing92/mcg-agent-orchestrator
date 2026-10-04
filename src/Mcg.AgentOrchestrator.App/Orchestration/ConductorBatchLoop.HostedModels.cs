using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private IReadOnlySet<GoalId> ServiceStewardAndAuthor(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        var changed = new HashSet<GoalId>(ServiceSteward(kernel, onlyGoalId));
        changed.UnionWith(ServiceAuthor(kernel, onlyGoalId));
        changed.UnionWith(ServiceStoreEvidence(kernel, onlyGoalId));
        return changed;
    }

    private void StopStewardAndAuthor()
    {
        StopSteward();
        StopAuthor();
    }
}
