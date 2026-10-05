using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorBoardFillHost? _boardFill;
    internal ConductorBatchLoop WithBoardFill(ConductorBoardFillHost host)
    {
        _boardFill = host;
        return this;
    }

    private void ServiceBoardFill(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        try { _boardFill?.ServiceTick(kernel, onlyGoalId); }
        catch (Exception exception)
        {
            EmitProgress($"BOARD_FILL result=service-failed error={exception.GetType().Name} reason={SanitizeReason(exception.Message)}");
        }
    }

    private void StopBoardFill()
    {
        try { _boardFill?.Stop(); }
        catch (Exception exception) { TryWriteAbnormalExitDiagnosticFailure("board-fill stop", exception); }
    }
}
