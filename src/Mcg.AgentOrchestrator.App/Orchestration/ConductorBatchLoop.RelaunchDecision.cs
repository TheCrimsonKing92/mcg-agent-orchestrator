using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private void EmitHomeBuildHeld(int tick, HomeBuildAdoptionRecord candidate, IReadOnlyList<string> blockers)
    {
        var identity = $"commit={candidate.AdoptedCommit} sequence={candidate.Sequence} blockers={string.Join(',', blockers)}";
        if (_homeBuildHeld == identity) return;
        _homeBuildHeld = identity;
        EmitProgress($"LOOP_RELAUNCH_HELD tick={tick} reason=home-build-adopted {identity}");
    }

    private static void EmitRelaunchNotRequired(
        int tick,
        ConductorLandingReceipt receipt,
        ConductorRelaunchDecision decision) =>
        EmitProgress($"LOOP_RELAUNCH_NOT_REQUIRED tick={tick} goal={receipt.GoalId} " +
            $"changedFiles={receipt.ChangedFiles.Count} classification={decision.Classification}");
}
