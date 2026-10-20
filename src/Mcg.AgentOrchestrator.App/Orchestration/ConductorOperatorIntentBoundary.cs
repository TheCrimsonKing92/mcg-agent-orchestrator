using System.Collections.Frozen;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Applies one captured batch; the tick owner retains checkpoint and completion authority.
internal static class ConductorOperatorIntentBoundary
{
    internal sealed record Result
    {
        public HashSet<GoalId> ChangedGoalIds { get; } = [];
        public HashSet<GoalId> AppliedGoalIds { get; } = [];
        public HashSet<string> UnavailableGoalIds { get; } = new(StringComparer.Ordinal);
        public HashSet<GoalId> DurableGoalIds { get; } = [];
        public HashSet<string> ActionableGoalIds { get; } = new(StringComparer.Ordinal);
        public List<string> Lines { get; } = [];
        public int AwaitingReload { get; set; }
    }

    internal static Result Apply(
        AgentOrchestratorKernel kernel, ConductorDriver driver, string? onlyGoalId,
        IReadOnlyDictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        OperatorIntentCoordinator? operatorIntents,
        Func<AgentOrchestratorKernel, GoalId, bool>? prepareTarget,
        Func<string, ConductorGoalReloadObservation> observeGoal)
    {
        var result = new Result();
        if (operatorIntents is null) return result;
        IReadOnlyList<OperatorIntentRecord> batch;
        try { batch = ConductorTickStepLedger.Measure("operator-intent-list", () => operatorIntents.CaptureGoalBatch()); }
        catch (Exception ex)
        {
            EmitProgress($"OPERATOR_INTENT result=store-unavailable phase=list reason={SanitizeReason(ex.Message)}");
            return result;
        }

        foreach (var target in batch.GroupBy(intent => intent.GoalId))
        {
            // Scope is admission, never authority to reject another conductor's command.
            if (onlyGoalId is not null && target.Key != onlyGoalId) continue;
            result.ActionableGoalIds.Add(target.Key);
            if (checkpointHeldGoals.ContainsKey(target.Key))
            {
                EmitProgress($"OPERATOR_INTENT goal={ShortGoalId(target.Key)} result=deferred reason=checkpoint-held");
                continue;
            }

            var ids = target.Select(intent => intent.Id).ToFrozenSet(StringComparer.Ordinal);
            var goalId = new GoalId(target.Key);
            var goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId);
            var before = goal is null ? null : GoalKernelChange.Capture(goal);
            try
            {
                // Uncommitted effects must survive target reload, including a failed completion write.
                var prepared = operatorIntents.HasPendingCompletion(target.Key) ||
                    (prepareTarget?.Invoke(kernel, goalId) ?? goal is not null);
                goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId);
                if (goal is not null && before != GoalKernelChange.Capture(goal))
                    result.ChangedGoalIds.Add(goalId);
                if (!prepared || goal is null)
                {
                    result.UnavailableGoalIds.Add(target.Key);
                    var disposition = UnloadedGoalIntentDisposition.Decide(target.Key, onlyGoalId, observeGoal);
                    if (disposition is UnloadedGoalIntentDisposition.Rejected rejection)
                        result.Lines.AddRange(operatorIntents.RejectPending(target.Key, rejection.Reason, rejection.ReasonCode, ids));
                    else
                    {
                        result.AwaitingReload++;
                        EmitProgress($"OPERATOR_INTENT goal={ShortGoalId(target.Key)} result=deferred reason=awaiting-goal-reload");
                    }
                    continue;
                }

                var application = OperatorIntentGoalApplication.ApplyPending(
                    kernel, goal, operatorIntents, driver.ParallelAcceptanceAttemptCoordinator, ids);
                result.Lines.AddRange(application.Lines);
                if (application.Result.MutatedGoalState || application.Result.RejectedAdjudication)
                    result.ChangedGoalIds.Add(goalId);
                if (application.Result.MutatedGoalState)
                    result.AppliedGoalIds.Add(goalId);
            }
            catch (Exception ex)
            {
                result.UnavailableGoalIds.Add(target.Key);
                // Preparation may reconcile target state before an external read fails.
                goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId);
                if (goal is not null && before != GoalKernelChange.Capture(goal))
                    result.ChangedGoalIds.Add(goalId);
                EmitProgress($"OPERATOR_INTENT goal={ShortGoalId(target.Key)} result=store-unavailable phase=execute reason={SanitizeReason(ex.Message)}");
            }
        }

        return result;
    }
}
