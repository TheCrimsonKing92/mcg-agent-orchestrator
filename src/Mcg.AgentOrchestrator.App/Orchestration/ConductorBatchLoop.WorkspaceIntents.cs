using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private IReadOnlyList<string> RetireUntilGoalLessons(AgentOrchestratorKernel kernel)
    {
        if (_operatorIntents is null) return [];
        try
        {
            var lines = _operatorIntents.RetireLandedUntilGoalLessons(kernel);
            foreach (var line in lines) EmitProgress(line);
            return lines;
        }
        catch (Exception ex)
        {
            EmitProgress($"LESSON_RETIREMENT result=store-unavailable reason={SanitizeReason(ex.Message)}");
            return [];
        }
    }

    private List<string> ServiceWorkspaceIntents(AgentOrchestratorKernel kernel)
    {
        if (_operatorIntents is null) return [];
        try
        {
            return [.. _operatorIntents.ExecuteWorkspacePending(kernel)];
        }
        catch (Exception ex)
        {
            EmitProgress($"OPERATOR_INTENT scope=workspace result=store-unavailable reason={SanitizeReason(ex.Message)}");
            return [];
        }
    }
}
