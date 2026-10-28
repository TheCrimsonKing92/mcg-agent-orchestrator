using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorIntentGoalApplicationResult(
    OperatorIntentExecutionResult Result,
    IReadOnlyList<string> Lines);

internal abstract record OperatorIntentOfflineOutcome
{
    internal sealed record Refused(string Message) : OperatorIntentOfflineOutcome;
    internal sealed record NothingPending : OperatorIntentOfflineOutcome;
    internal sealed record Applied(IReadOnlyList<string> Lines) : OperatorIntentOfflineOutcome;
    internal sealed record NotDurable(string Reason) : OperatorIntentOfflineOutcome;
}

internal static class OperatorIntentGoalApplication
{
    internal static OperatorIntentGoalApplicationResult ApplyPending(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OperatorIntentCoordinator intents,
        ConductorParallelAcceptanceAttemptCoordinator attempts,
        IReadOnlySet<string>? intentIds = null)
    {
        var result = intents.ExecutePending(kernel, goal, intentIds);
        var lines = new List<string>();
        if (result.MutatedGoalState && goal.Status != GoalStatus.Completed)
        {
            var invalidation = AcceptanceAttemptRetryInvalidation.Apply(
                kernel,
                goal,
                attempts,
                "Operator intent made an acceptance-verified task dispatchable; invalidated the current acceptance attempt before redispatch.");
            if (invalidation.Changed)
            {
                lines.Add(
                    $"ACCEPTANCE_INVALIDATED goal={ShortGoalId(goal.Id.Value)} attempt_staled={invalidation.AttemptInvalidated.ToString().ToLowerInvariant()} goal_reopened={invalidation.GoalReopened.ToString().ToLowerInvariant()}");
            }
            lines.AddRange(intents.ReopenedMemberGateAbandonment?.Apply(goal) ?? []);
        }

        lines.AddRange(result.ProgressLines);
        if (result.MutatedGoalState && !result.PreserveGoalHold && goal.Status != GoalStatus.Completed)
            kernel.ClearGoalHold(goal.Id);
        return new OperatorIntentGoalApplicationResult(result, lines);
    }

    // Use a fresh coordinator for each run so an undurable claim is evaluated against reloaded state.
    internal static async Task<OperatorIntentOfflineOutcome> ApplyOffline(
        OrchestratorWorkspace workspace,
        ITransactionalOrchestratorStateRepository repository,
        OperatorIntentCoordinator intents,
        ConductorParallelAcceptanceAttemptCoordinator attempts,
        GoalId goalId)
    {
        ConductorLoopLease lease;
        try
        {
            lease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        }
        catch (InvalidOperationException ex)
        {
            return new OperatorIntentOfflineOutcome.Refused(ex.Message);
        }

        using (lease)
        {
            // The checkpoint compares persisted JSON. Domain restore can normalize it
            // (for example, timeline order), so its re-export is not the stored baseline.
            // Read before hydration so an intervening write still fails RejectConflict.
            var baseline = await repository.LoadGoalAsync(goalId);
            if (baseline is null)
                return new OperatorIntentOfflineOutcome.NothingPending();

            var kernel = await repository.LoadGoalsAsync([goalId]);
            var goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId);
            if (goal is null)
                return new OperatorIntentOfflineOutcome.NothingPending();

            var publicationBound = false;
            if (goal.OutstandingPostLandingDuties.Any(item => item.Publication is null) &&
                await repository.ReadPromotionFactAsync(goalId) is { } publication)
            {
                kernel.BindPostLandingPublication(goalId, publication);
                publicationBound = goal.CriterionEvidenceObligations.Any(item => item.Publication is not null &&
                    baseline.CriterionEvidenceObligations?.Any(prior => prior.Id == item.Id && prior.Publication is null) == true);
            }
            var application = ApplyPending(kernel, goal, intents, attempts);
            if (!application.Result.MutatedGoalState && !publicationBound)
            {
                return application.Lines.Count == 0
                    ? new OperatorIntentOfflineOutcome.NothingPending()
                    : new OperatorIntentOfflineOutcome.Applied(application.Lines);
            }

            var humanInputRequests = kernel.ExportSnapshot().HumanInputRequests
                .Where(request => request.GoalId == goalId.Value).ToArray();
            var results = await repository.SaveGoalSnapshotsWithMergeAsync(
                [new GoalSnapshotSaveRequest(
                    baseline,
                    kernel.ExportGoalSnapshot(goalId),
                    humanInputRequests,
                    RejectConflict: true)]);
            var saved = results.Single(result => result.GoalId == goalId.Value);
            if (saved.Disposition != GoalSnapshotSaveDisposition.Saved)
                return new OperatorIntentOfflineOutcome.NotDurable(saved.Message);

            intents.CompletePersisted([goalId]);
            return new OperatorIntentOfflineOutcome.Applied(application.Lines);
        }
    }

    private static string ShortGoalId(string goalId) =>
        goalId.Length <= 8 ? goalId : goalId[..8];
}
