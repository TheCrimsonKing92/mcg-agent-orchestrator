using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private IReadOnlySet<GoalId> ServiceStewardAndAuthor(AgentOrchestratorKernel kernel, string? onlyGoalId)
    {
        var changed = new HashSet<GoalId>(ConductorTickStepLedger.Measure("steward", () => ServiceSteward(kernel, onlyGoalId)));
        changed.UnionWith(ConductorTickStepLedger.Measure("author", () => ServiceAuthor(kernel, onlyGoalId)));
        changed.UnionWith(ConductorTickStepLedger.Measure("store-evidence", () => ServiceStoreEvidence(kernel, onlyGoalId)));
        ConductorTickStepLedger.Measure("judge-panel", () => ServiceJudgePanel(kernel, onlyGoalId));
        ConductorTickStepLedger.Measure("board-fill", () => ServiceBoardFill(kernel, onlyGoalId));
        return changed;
    }

    private void StopStewardAndAuthor()
    {
        StopSteward();
        StopAuthor();
        StopJudgePanel();
        StopBoardFill();
    }
}
