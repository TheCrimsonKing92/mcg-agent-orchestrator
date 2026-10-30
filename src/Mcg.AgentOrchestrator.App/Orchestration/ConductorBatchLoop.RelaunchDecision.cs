using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static void EmitRelaunchNotRequired(
        int tick,
        ConductorLandingReceipt receipt,
        ConductorRelaunchDecision decision) =>
        EmitProgress($"LOOP_RELAUNCH_NOT_REQUIRED tick={tick} goal={receipt.GoalId} " +
            $"changedFiles={receipt.ChangedFiles.Count} classification={decision.Classification}");
}
