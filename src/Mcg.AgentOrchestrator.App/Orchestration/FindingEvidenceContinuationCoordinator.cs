using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns selection, bounded routing and launch validation for a held review evidence transition.
internal static class FindingEvidenceContinuationCoordinator
{
    internal static bool TrySelectFindingEvidenceContinuation(ConductorDriver driver, Goal goal, TaskSpec reviewer,
        out FindingEvidenceReviewContinuation? continuation, out string reason)
    {
        continuation = null;
        reason = "candidate-unavailable";
        var identity = driver._resolveCandidateIdentity(goal).Identity;
        var sha = driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (identity is null || !ConductorGitRevisionReader.IsValid(sha)) return false;
        var settings = driver._getFindingEvidenceEngineSettings(goal);
        var selected = FindingEvidenceReceiptSelector.TrySelectReviewContinuation(goal, reviewer, sha!, identity,
            request => TryNormalizeFindingEvidenceRequest(request, settings,
                (project, testClass) => driver._resolveFindingEvidenceSiblingClasses(goal, project, testClass),
                out var normalized, out _, out _, out _) ? normalized : null,
            BuildFindingEvidenceExecutionBasisIdentity(settings), out continuation, out reason);
        // Equivalent copies retain the exact batch already claimed for delivery.
        if (selected && reviewer.FindingEvidenceReviewContinuation is { } claimed && claimed.Key == continuation!.Key)
        {
            if (!FindingEvidenceReceiptSelector.ClaimedSelectionsRemainValid(goal, claimed, continuation!,
                    BuildFindingEvidenceExecutionBasisIdentity(settings)))
            {
                continuation = null;
                reason = "claimed-review-evidence-unavailable";
                return false;
            }
            continuation = claimed;
        }
        else if (selected && reviewer.FindingEvidenceReviewContinuation?.DecisionKey == continuation!.DecisionKey)
        {
            continuation = null;
            reason = "claimed-review-evidence-was-superseded";
            return false;
        }
        return selected;
    }

    internal static bool TryObserveFindingEvidenceContinuation(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy,
        out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if ((driver._cohortKernel ?? driver._conductorTickKernel) is null) return false;
        foreach (var reviewer in goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer &&
                     task.Status == WorkTaskStatus.Failed && task.LastProcess is not { IsRunning: true }))
        {
            if (goal.Tasks.Any(task => DispatchReadinessRules.IsEarlierSdlcStageOf(task.RequiredRole, reviewer.RequiredRole) &&
                    task.Status != WorkTaskStatus.Completed)) continue;
            if (!TrySelectFindingEvidenceContinuation(driver, goal, reviewer, out var continuation, out _)) continue;
            var cap = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier);
            if (cap.Round >= policy.ReviewAutoRetryStopRound || cap.LifetimeRound >= cap.LifetimeBackstop)
                return false;
            observation = FailedGoalFindingObservation.Routed(
                FailedGoalFindingObservationKind.FindingRouteObserved, reviewer.Id,
                BuildFailedGoalAttemptIdentity(reviewer), continuation!.Render(), null,
                observedCause: RetryCause.NewTestFinding) with { EvidenceContinuation = continuation };
            return true;
        }
        return false;
    }

    internal static bool FindingEvidenceContinuationInvalid(ConductorDriver driver, Goal goal, TaskSpec task) =>
        FindingEvidenceContinuationInvalid(driver, goal, task, out _);

    private static bool FindingEvidenceContinuationInvalid(ConductorDriver driver, Goal goal, TaskSpec task, out string reason)
    {
        reason = string.Empty;
        if (task.PendingRetryCause != RetryCause.NewTestFinding || task.FindingEvidenceReviewContinuation is not { } claimed)
            return false;
        if (!TrySelectFindingEvidenceContinuation(driver, goal, task, out var current, out reason)) return true;
        if (current!.Key == claimed.Key && current.Selections.Select(selection => selection.Canonical).Order(StringComparer.Ordinal)
                .SequenceEqual(claimed.Selections.Select(selection => selection.Canonical).Order(StringComparer.Ordinal))) return false;
        reason = "claimed-review-evidence-selection-changed";
        return true;
    }

    internal static ConductorAdvanceOutcome.Held? HoldInvalidFindingEvidenceContinuation(
        ConductorDriver driver, Goal goal, IEnumerable<TaskSpec> tasks, GoalLifecycleState state, string reason)
    {
        foreach (var task in tasks)
        {
            if (!FindingEvidenceContinuationInvalid(driver, goal, task, out var failure)) continue;
            var claimed = task.FindingEvidenceReviewContinuation!;
            return new ConductorAdvanceOutcome.Held(state, reason)
            {
                Decision = new PolicyDecisionRecord("finding-evidence-continuation", "Hold", 0, failure, reason,
                    Array.AsReadOnly<PolicyDecisionFact>(
                    [
                        new PolicyDecisionFact("goalId", goal.Id.Value),
                        new PolicyDecisionFact("taskId", task.Id.Value),
                        new PolicyDecisionFact("candidateSha", claimed.CandidateSha),
                        new PolicyDecisionFact("candidateIdentity", claimed.CandidateIdentity.Canonical),
                        new PolicyDecisionFact("decisionKey", claimed.DecisionKey),
                        new PolicyDecisionFact("continuationKey", claimed.Key),
                        new PolicyDecisionFact("priorVerificationAt", claimed.PriorVerificationAt.ToString("O")),
                        new PolicyDecisionFact("priorDispatchAt", claimed.PriorDispatchAt.ToString("O")),
                        new PolicyDecisionFact("selections", JsonSerializer.Serialize(claimed.Selections))
                    ]))
            };
        }
        return null;
    }

    internal static void PrepareReadyFindingEvidenceContinuations(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy)
    {
        var kernel = driver._cohortKernel ?? driver._conductorTickKernel;
        if (kernel is null) return;
        foreach (var task in ReadyCandidateTasks(goal).Where(task => task.RequiredRole == AgentRole.Reviewer))
        {
            if (!TrySelectFindingEvidenceContinuation(driver, goal, task, out var continuation, out _) ||
                task.FindingEvidenceReviewContinuation?.DecisionKey == continuation!.DecisionKey &&
                    task.PendingRetryCause == RetryCause.NewTestFinding ||
                task.DispatchHistory.Any(dispatch => dispatch.FindingEvidenceReviewContinuation?.DecisionKey == continuation.DecisionKey)) continue;
            var cap = ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier);
            if (cap.Round >= policy.ReviewAutoRetryStopRound || cap.LifetimeRound >= cap.LifetimeBackstop) continue;
            kernel.RecordFindingEvidenceReviewContinuation(goal.Id, task.Id, continuation);
            driver._retryTask(goal.Id, task.Id, continuation.Render(), null, RetryCause.NewTestFinding);
        }
    }

    internal static Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>?
        FindingEvidenceDispatchCheckpoint(ConductorDriver driver, CriticalDispatchLifecycleCheckpoint? checkpoint) => checkpoint is null ? null :
        (kernel, goalId, taskId, phase) =>
        {
            var current = kernel.GetGoal(goalId);
            if (FindingEvidenceContinuationInvalid(driver, current, kernel.GetTask(goalId, taskId)))
                throw new InvalidOperationException("Selected finding evidence no longer authorizes this Reviewer continuation.");
            checkpoint.BeforeWorkerStart(kernel, goalId, taskId, phase);
        };

    internal static void RecordObservation(ConductorDriver driver, Goal goal, TaskId targetTaskId,
        FailedGoalRecoveryDecision decision, FailedGoalFindingObservation? observation)
    {
        if (observation?.EvidenceContinuation is { } continuation &&
            targetTaskId.Value == continuation.TaskId && decision.RetryCause == RetryCause.NewTestFinding)
        {
            (driver._cohortKernel ?? driver._conductorTickKernel ?? throw new InvalidOperationException("Continuation requires conductor state ownership."))
                .RecordFindingEvidenceReviewContinuation(goal.Id, targetTaskId, continuation);
        }
    }
}
