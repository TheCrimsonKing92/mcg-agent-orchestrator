using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorAuthorHost? _author;

    internal ConductorBatchLoop WithAuthor(ConductorAuthorHost author)
    {
        _author = author;
        return this;
    }

    private IReadOnlySet<GoalId> ServiceAuthor(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        if (_author is null) return new HashSet<GoalId>();
        try { return _author.ServiceTick(kernel, onlyGoalId); }
        catch (Exception ex)
        {
            EmitProgress($"AUTHOR result=service-failed error={ex.GetType().Name} reason={SanitizeReason(ex.Message)}");
            throw;
        }
    }

    private void StopAuthor()
    {
        try { _author?.Stop(); }
        catch (Exception ex) { TryWriteAbnormalExitDiagnosticFailure("author stop", ex); }
    }
}
