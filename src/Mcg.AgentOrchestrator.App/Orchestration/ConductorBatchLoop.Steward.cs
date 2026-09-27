using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorStewardHost? _steward;

    internal ConductorBatchLoop WithSteward(ConductorStewardHost steward)
    {
        _steward = steward;
        return this;
    }

    private IReadOnlySet<GoalId> ServiceSteward(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (_steward is null) return new HashSet<GoalId>();
        try
        {
            return _steward.ServiceTick(kernel, onlyGoalId);
        }
        catch (Exception ex)
        {
            EmitProgress($"STEWARD result=store-unavailable reason={SanitizeReason(ex.Message)}");
            return new HashSet<GoalId>();
        }
    }

    private void StopSteward() => _steward?.Stop();
}
