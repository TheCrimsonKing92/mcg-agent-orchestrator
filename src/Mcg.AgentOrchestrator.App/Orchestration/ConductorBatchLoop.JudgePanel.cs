using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorJudgePanelHost? _judgePanel;
    internal ConductorBatchLoop WithJudgePanel(ConductorJudgePanelHost panel)
    {
        _judgePanel = panel;
        return this;
    }
    private void ServiceJudgePanel(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        try { _judgePanel?.ServiceTick(kernel, onlyGoalId); }
        catch (Exception exception)
        {
            EmitProgress($"JUDGE_PANEL result=service-failed error={exception.GetType().Name} reason={SanitizeReason(exception.Message)}");
        }
    }
    private void StopJudgePanel()
    {
        try { _judgePanel?.Stop(); }
        catch (Exception exception) { TryWriteAbnormalExitDiagnosticFailure("judge-panel stop", exception); }
    }
}
