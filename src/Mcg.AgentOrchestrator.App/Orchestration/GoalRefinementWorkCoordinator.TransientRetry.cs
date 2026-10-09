using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SpecRefinementTransientRetryOutcome(
    int Attempts,
    GoalRefinementWorkLaunchResult Launch,
    string? ExhaustionDetail = null);

internal static partial class GoalRefinementWorkCoordinator
{
    internal const int ConsecutiveTransientRetryLimit = 3;
    internal const string TransientRetryExhaustedReason = "SPEC_REFINEMENT_TRANSIENT_RETRY_EXHAUSTED";

    internal static SpecRefinementTransientRetryOutcome AdvanceTransientRetry(
        OrchestratorWorkspace workspace, GoalId goalId, OrchestratorStateOutboxState failedState)
    {
        var store = SpecRefinementTransientRetryStore.ForWorkspace(workspace);
        var retry = store.Get(goalId);
        // Completion removes the outbox row. A subsequent receipt starts a new retry episode,
        // including when the previous receipt was completed by the hand-run verb.
        if (retry?.MessageId != failedState.Message.Id ||
            retry.MessageCreatedAt != failedState.Message.CreatedAt)
            retry = null;
        retry ??= new SpecRefinementTransientRetry(
            failedState.Message.Id, failedState.Message.CreatedAt, 0, failedState.Detail!, null);

        var launchStore = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace);
        var launchAttempt = launchStore.Get(goalId);
        var now = UtcNow();
        if (retry.Attempts >= ConsecutiveTransientRetryLimit)
        {
            var decision = SpecRefinementLaunchPolicy.Decide(
                launchAttempt, now, RecoveryLaunchCadence, ConsecutiveFailedClaimLimit);
            if (retry.ExhaustedAt is null && decision.Kind == SpecRefinementLaunchDecisionKind.DeferCadence)
                return new(retry.Attempts, new(false, null, CadenceDeferredDetail));

            var detail = $"{TransientRetryExhaustedReason} goal={goalId.Value} " +
                $"attempts={retry.Attempts} limit={ConsecutiveTransientRetryLimit} detail={failedState.Detail}";
            if (retry.ExhaustedAt is null)
            {
                store.Save(goalId, retry with { ExhaustedAt = now, LastFailureDetail = failedState.Detail! });
                new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, integrationBranch: workspace.IntegrationBranch).AppendGoalEscalated(
                    goalId, GoalLifecycleState.WorkspaceReady, detail, "spec-refinement-transient-retry");
            }
            return new(retry.Attempts, new(false, null, detail), detail);
        }

        // A recorded failure proves the executor claimed its work; claim-miss accounting
        // must not pre-empt the transient retry bound. Preserve the actual launch cadence.
        if (launchAttempt is { EscalatedAt: null, ConsecutiveFailedClaims: > 0 })
            launchStore.Save(goalId, launchAttempt with { ConsecutiveFailedClaims = 0 });

        var launch = TryLaunchIfDue(workspace, goalId, failedState.Status, failedState.ProcessingStartedAt);
        if (launch.Started)
        {
            retry = retry with { Attempts = checked(retry.Attempts + 1), LastFailureDetail = failedState.Detail! };
            store.Save(goalId, retry);
        }
        return new(retry.Attempts, launch);
    }
}
