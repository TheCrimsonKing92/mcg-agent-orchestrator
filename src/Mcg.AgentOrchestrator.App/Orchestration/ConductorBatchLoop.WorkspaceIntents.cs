using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private List<string> ServiceWorkspaceIntents(AgentOrchestratorKernel kernel)
    {
        if (_operatorIntents is null) return [];
        try
        {
            return [.. _operatorIntents.ExecuteWorkspacePending(kernel)];
        }
        catch (Exception ex)
        {
            return [$"OPERATOR_INTENT scope=workspace result=store-unavailable reason={SanitizeReason(ex.Message)}"];
        }
    }
}
