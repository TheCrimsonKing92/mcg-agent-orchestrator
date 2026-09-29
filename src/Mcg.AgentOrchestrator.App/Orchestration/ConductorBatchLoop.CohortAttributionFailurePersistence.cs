using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static ParallelLandingOutcome TrackCohortAttributionFailure(
        ConductorAcceptanceCohortRunResult run,
        KeyValuePair<string, Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceResult> member,
        AgentOrchestratorKernel kernel,
        HashSet<GoalId> changedGoalIds)
    {
        if (run.Receipt is { } cohort &&
            cohort.AttributedMembers.Any(attributed => attributed.GoalId.Value == member.Key) &&
            kernel.Goals.SingleOrDefault(goal => goal.Id.Value == member.Key) is { } goal &&
            goal.RetainedAcceptanceFailure?.CheckAttributions?.Any(attribution =>
                attribution.Evidence.StartsWith(
                    $"{ConductorAcceptanceCohortAttributionVerdicts.EvidencePrefix}{cohort.Identity.Value} cohort-partition=",
                    StringComparison.Ordinal)) == true)
        {
            changedGoalIds.Add(goal.Id);
        }
        return new ParallelLandingOutcome(member.Value, SlotIndex: 0);
    }
}
