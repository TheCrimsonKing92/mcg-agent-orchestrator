using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorStoreEvidenceStep? _storeEvidence;

    internal ConductorBatchLoop WithStoreEvidence(ConductorStoreEvidenceStep step)
    {
        _storeEvidence = step;
        return this;
    }

    private IReadOnlySet<GoalId> ServiceStoreEvidence(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (_storeEvidence is null) return new HashSet<GoalId>();
        try { return _storeEvidence.ServiceTick(kernel, onlyGoalId); }
        catch (Exception ex)
        {
            EmitProgress($"STORE_EVIDENCE result=service-failed error={ex.GetType().Name} reason={SanitizeReason(ex.Message)}");
            throw;
        }
    }
}
