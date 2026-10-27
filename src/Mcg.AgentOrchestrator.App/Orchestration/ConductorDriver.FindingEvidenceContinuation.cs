using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private bool TrySelectFindingEvidenceContinuation(Goal goal, TaskSpec reviewer,
        out FindingEvidenceReviewContinuation? continuation, out string reason)
    {
        continuation = null;
        reason = "candidate-unavailable";
        var identity = _resolveCandidateIdentity(goal).Identity;
        var sha = _getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (identity is null || !ConductorGitRevisionReader.IsValid(sha)) return false;
        var settings = _getFindingEvidenceEngineSettings(goal);
        var selected = FindingEvidenceReceiptSelector.TrySelectReviewContinuation(goal, reviewer, sha!, identity,
            request => TryNormalizeFindingEvidenceRequest(request, settings,
                (project, testClass) => _resolveFindingEvidenceSiblingClasses(goal, project, testClass),
                out var normalized, out _, out _, out _) ? normalized : null,
            BuildFindingEvidenceExecutionBasisIdentity(settings), out continuation, out reason);
        // Equivalent copies retain the exact batch already claimed for delivery.
        if (selected && reviewer.FindingEvidenceReviewContinuation is { } claimed && claimed.Key == continuation!.Key)
            continuation = claimed;
        else if (selected && reviewer.FindingEvidenceReviewContinuation?.DecisionKey == continuation!.DecisionKey)
        {
            continuation = null;
            reason = "claimed-review-evidence-was-superseded";
            return false;
        }
        return selected;
    }

    private bool TryObserveFindingEvidenceContinuation(Goal goal, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if ((_cohortKernel ?? _conductorTickKernel) is null) return false;
        foreach (var reviewer in goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer &&
                     task.Status == WorkTaskStatus.Failed && task.LastProcess is not { IsRunning: true }))
        {
            if (goal.Tasks.Any(task => DispatchReadinessRules.IsEarlierSdlcStageOf(task.RequiredRole, reviewer.RequiredRole) &&
                    task.Status != WorkTaskStatus.Completed)) continue;
            if (!TrySelectFindingEvidenceContinuation(goal, reviewer, out var continuation, out _)) continue;
            var cap = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier);
            if (cap.Round >= policy.ReviewAutoRetryStopRound || cap.LifetimeRound >= cap.LifetimeBackstop)
                return false;
            observation = FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded, reviewer.Id,
                BuildFailedGoalAttemptIdentity(reviewer), continuation!.Render(), null,
                observedCause: RetryCause.NewTestFinding) with { EvidenceContinuation = continuation };
            return true;
        }
        return false;
    }

    private bool FindingEvidenceContinuationInvalid(Goal goal, TaskSpec task) =>
        task.PendingRetryCause == RetryCause.NewTestFinding && task.FindingEvidenceReviewContinuation is { } claimed &&
        (!TrySelectFindingEvidenceContinuation(goal, task, out var current, out _) || current!.Key != claimed.Key ||
         !current.Selections.Select(selection => selection.Canonical).Order(StringComparer.Ordinal)
             .SequenceEqual(claimed.Selections.Select(selection => selection.Canonical).Order(StringComparer.Ordinal)));

    private void PrepareReadyFindingEvidenceContinuations(Goal goal, ConductorAutonomyPolicy policy)
    {
        var kernel = _cohortKernel ?? _conductorTickKernel;
        if (kernel is null) return;
        foreach (var task in ReadyCandidateTasks(goal).Where(task => task.RequiredRole == AgentRole.Reviewer))
        {
            if (!TrySelectFindingEvidenceContinuation(goal, task, out var continuation, out _) ||
                task.FindingEvidenceReviewContinuation?.DecisionKey == continuation!.DecisionKey &&
                    task.PendingRetryCause == RetryCause.NewTestFinding ||
                task.DispatchHistory.Any(dispatch => dispatch.FindingEvidenceReviewContinuation?.DecisionKey == continuation.DecisionKey)) continue;
            var cap = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier);
            if (cap.Round >= policy.ReviewAutoRetryStopRound || cap.LifetimeRound >= cap.LifetimeBackstop) continue;
            kernel.RecordFindingEvidenceReviewContinuation(goal.Id, task.Id, continuation);
            _retryTask(goal.Id, task.Id, continuation.Render(), null, RetryCause.NewTestFinding);
        }
    }

    private Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>?
        FindingEvidenceDispatchCheckpoint(CriticalDispatchLifecycleCheckpoint? checkpoint) => checkpoint is null ? null :
        (kernel, goalId, taskId, phase) =>
        {
            var current = kernel.GetGoal(goalId);
            if (FindingEvidenceContinuationInvalid(current, kernel.GetTask(goalId, taskId)))
                throw new InvalidOperationException("Selected finding evidence no longer authorizes this Reviewer continuation.");
            checkpoint.BeforeWorkerStart(kernel, goalId, taskId, phase);
        };
}
