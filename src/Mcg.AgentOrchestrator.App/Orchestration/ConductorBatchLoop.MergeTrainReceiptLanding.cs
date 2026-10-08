using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<string> _reportedStaleMergeTrainReceiptIds = new(StringComparer.Ordinal);
    private readonly PassedMergeTrainReceiptHolds _passedMergeTrainReceiptHolds = new();

    private (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates)
        LandPassedMergeTrainReceiptsBeforeAdmission(
            ConductorDriver driver,
            ConductorAutonomyPolicy policy,
            Goal[] eligible,
            ConductorSpeculativeAcceptanceCandidate[] candidates,
            IReadOnlyList<Goal> scopedGoals,
            Dictionary<string, ParallelLandingOutcome> results,
            int tick,
            List<string> changedGoalLines)
    {
        return PassedMergeTrainReceiptAdmission.Apply(
            driver, policy, eligible, candidates, scopedGoals, results, tick, changedGoalLines,
            _passedMergeTrainReceiptHolds, _reportedStaleMergeTrainReceiptIds, line => EmitProgress(line),
            RecordParallelAcceptanceProgress,
            (goal, reason) => ParallelAcceptanceHeld(goal, policy, reason, ConductorHoldOwner.AcceptanceQueue));
    }
}
