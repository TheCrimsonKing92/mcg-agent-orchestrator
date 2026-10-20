using System.Collections.Frozen;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private IReadOnlySet<string> ProtectedOperatorIntentGoalIds(
        AgentOrchestratorKernel kernel, Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals) =>
        checkpointHeldGoals.Keys.Concat(kernel.Goals
                .Where(goal => _operatorIntents?.HasPendingCompletion(goal.Id.Value) == true)
                .Select(goal => goal.Id.Value))
            .ToHashSet(StringComparer.Ordinal);

    private sealed record OperatorIntentBoundaryResult
    {
        public HashSet<GoalId> ChangedGoalIds { get; } = [];
        public HashSet<GoalId> AppliedGoalIds { get; } = [];
        public HashSet<string> UnavailableGoalIds { get; } = new(StringComparer.Ordinal);
        public HashSet<GoalId> DurableGoalIds { get; } = [];
        public HashSet<string> ActionableGoalIds { get; } = new(StringComparer.Ordinal);
        public List<string> Lines { get; } = [];
        public int AwaitingReload { get; set; }
    }

    private OperatorIntentBoundaryResult ServiceOperatorIntentBoundary(
        AgentOrchestratorKernel kernel, ConductorDriver driver, string? onlyGoalId,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick, Action<AgentOrchestratorKernel>? persistTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<TimeSpan>? busyWriteDelay)
    {
        var result = new OperatorIntentBoundaryResult();
        if (_operatorIntents is null) return result;
        IReadOnlyList<OperatorIntentRecord> batch;
        try { batch = ConductorTickStepLedger.Measure("operator-intent-list", () => _operatorIntents.CaptureGoalBatch()); }
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
                var prepared = _operatorIntents.HasPendingCompletion(target.Key) ||
                    (_prepareOperatorIntentTarget?.Invoke(kernel, goalId) ?? goal is not null);
                goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == goalId);
                if (goal is not null && before != GoalKernelChange.Capture(goal))
                    result.ChangedGoalIds.Add(goalId);
                if (!prepared || goal is null)
                {
                    result.UnavailableGoalIds.Add(target.Key);
                    var disposition = UnloadedGoalIntentDisposition.Decide(target.Key, onlyGoalId, _goalReloadObservation);
                    if (disposition is UnloadedGoalIntentDisposition.Rejected rejection)
                        result.Lines.AddRange(_operatorIntents.RejectPending(target.Key, rejection.Reason, rejection.ReasonCode, ids));
                    else
                    {
                        result.AwaitingReload++;
                        EmitProgress($"OPERATOR_INTENT goal={ShortGoalId(target.Key)} result=deferred reason=awaiting-goal-reload");
                    }
                    continue;
                }

                var application = OperatorIntentGoalApplication.ApplyPending(
                    kernel, goal, _operatorIntents, driver.ParallelAcceptanceAttemptCoordinator, ids);
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

        if (result.ChangedGoalIds.Count == 0) return result;
        var requested = result.ChangedGoalIds.ToArray();
        if (checkpointGoalTick is not null)
            result.DurableGoalIds.UnionWith(ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, requested), requested, checkpointHeldGoals, tick,
                "operator-intent", result.Lines));
        else if (persistGoalTick is not null)
        {
            PersistGoalTickOrThrow(persistGoalTick, kernel, requested, tick, result.Lines, busyWriteDelay);
            result.DurableGoalIds.UnionWith(requested);
        }
        else if (TryPersistTick(persistTick, kernel, tick, ResolveGoalContext(requested, onlyGoalId),
                     "operator-intent", result.Lines, busyWriteDelay))
            result.DurableGoalIds.UnionWith(requested);

        if (result.DurableGoalIds.Count > 0)
            CompletePersistedOperatorIntents(result.DurableGoalIds, result.Lines);
        return result;
    }
}
