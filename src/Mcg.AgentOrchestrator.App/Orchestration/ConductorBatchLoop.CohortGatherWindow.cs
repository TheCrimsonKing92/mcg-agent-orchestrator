using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly ConductorAcceptanceCohortGatherWindow _cohortGatherWindow = new();
    private DateTimeOffset? _cohortGatherDeadline;

    private static DateTimeOffset? ComputeCohortGatherDeadline(DateTimeOffset started, TimeSpan? maxDuration)
    {
        if (maxDuration is not { } duration)
            return null;
        if (duration <= TimeSpan.Zero)
            return started;
        return duration >= DateTimeOffset.MaxValue - started ? null : started + duration;
    }

    private void HoldLoneReadyGoalForInReviewCohortPartner(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> scopedGoals,
        IReadOnlyList<Goal> orderedEligible,
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> productionCandidates,
        LiveAcceptanceCensus acceptanceCensus,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        Dictionary<string, ParallelLandingOutcome> results,
        int tick,
        List<string> changedGoalLines)
    {
        var readyCandidates = productionCandidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready)
            .ToArray();
        _cohortGatherWindow.ObserveReadyGoals(readyCandidates.Select(candidate => candidate.GoalId.Value).ToArray());
        if (readyCandidates.Length != 1)
            return;

        var readyCandidate = readyCandidates[0];
        var ready = orderedEligible.Single(goal => goal.Id == readyCandidate.GoalId);
        var now = _utcNow();
        var inReview = scopedGoals
            .Where(goal => goal.Id != ready.Id &&
                goal.Status != GoalStatus.Completed &&
                !GoalStatusSemantics.ExcludesFromConductorWorkingSet(goal.Status) &&
                goal.Tasks.Any(task => task.RequiredRole == AgentRole.Reviewer &&
                    task.Status == WorkTaskStatus.Running && task.LastDispatch is not null))
            .ToArray();
        if (results.ContainsKey(ready.Id.Value) ||
            !driver.AcceptanceCohortsEnabled ||
            acceptanceCensus.CaptureFailure is not null ||
            acceptanceCensus.OccupiedCount != 0 ||
            policy.AcceptanceCohortGatherWindowSeconds <= 0 ||
            inReview.Length == 0 ||
            (_cohortGatherDeadline is { } deadline &&
                deadline - now <= TimeSpan.FromSeconds(policy.AcceptanceCohortGatherWindowSeconds)) ||
            IsAcceptanceEngineCircuitHoldRequired(ready.Status, _acceptanceEngineCircuit?.Read()) ||
            !kernel.BuildVerificationGate(ready.Id).IsSatisfied)
        {
            _cohortGatherWindow.Evaluate(ready.Id.Value, [], now, 0);
            return;
        }

        var suppressedPairs = driver.ReadSuppressedGroupedPairs();
        var trainImplicatedKeys = driver.ReadTrainImplicatedMemberKeys();
        var partners = inReview
            .Where(goal => ConductorAcceptanceCohortSelector.Select(
                [readyCandidate, new ConductorSpeculativeAcceptanceCandidate(
                    goal.Id, driver.ProjectInReviewCohortPartner(goal, policy))],
                suppressedPairFingerprints: suppressedPairs,
                trainImplicatedMemberKeys: trainImplicatedKeys).Selection is not null)
            .Select(goal => goal.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var decision = _cohortGatherWindow.Evaluate(
            ready.Id.Value, partners, now, policy.AcceptanceCohortGatherWindowSeconds);
        if (!decision.Hold)
            return;

        results[ready.Id.Value] = new ParallelLandingOutcome(
            ParallelAcceptanceHeld(ready, policy, "Gathering a compatible in-review acceptance cohort partner."),
            SlotIndex: null);
        RecordParallelAcceptanceProgress(
            $"ACCEPTANCE_COHORT tick={tick} outcome=gathering goal={ready.Id.Value[..8]} " +
            $"partners={string.Join(',', partners.Select(id => id[..8]))} remaining_seconds={decision.RemainingSeconds}",
            changedGoalLines);
    }
}
