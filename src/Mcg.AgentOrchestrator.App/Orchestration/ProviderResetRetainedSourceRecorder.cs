using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ProviderResetRetainedSourceRecorder(Func<ITransactionalOrchestratorStateRepository> repository)
{
    internal bool Apply(AgentOrchestratorKernel kernel, OperatorIntentRecord intent, DateTimeOffset now)
    {
        if (intent.GoalId != OperatorIntentScopes.Workspace)
            throw new InvalidOperationException("provider-reset-workspace-scope-required");
        if (kernel.ProviderHoldScope.SelectMany(goal => goal.Timeline).Any(evt =>
                ProviderBudgetHoldSchedules.TryParseReset(evt, out var marker) && marker.IntentId == intent.Id))
            return true;
        var payload = JsonSerializer.Deserialize<ProviderResetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
            ?? throw new InvalidOperationException("provider-reset requires a payload.");
        if (!ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(kernel.ProviderHoldScope, payload.BindingKey, out var hold))
            throw new InvalidOperationException("no-current-hold");
        if (payload.ResetAt > now.AddDays(31)) throw new InvalidOperationException("reset-too-far");
        if (kernel.Goals.Any(goal => goal.Id == hold.SourceGoalId))
            throw new InvalidOperationException("source-goal-in-working-set");

        var result = repository().TransactGoalAsync<(bool Replayed, GoalSnapshot? Snapshot)>(
            "provider-reset-retained-source", hold.SourceGoalId, (stored, _) =>
            {
                if (stored is null)
                    return Task.FromResult<(bool, GoalSnapshot?, (bool, GoalSnapshot?))>((false, null, (false, null)));
                var scratch = new AgentOrchestratorKernel(new FixedClock(now));
                scratch.ReplaceGoalWithSnapshot(stored);
                if (scratch.GetGoal(hold.SourceGoalId).Timeline.Any(evt =>
                        ProviderBudgetHoldSchedules.TryParseReset(evt, out var marker) && marker.IntentId == intent.Id))
                    return Task.FromResult<(bool, GoalSnapshot?, (bool, GoalSnapshot?))>((false, null, (true, stored)));

                scratch.RecordTaskNote(hold.SourceGoalId, hold.SourceTaskId,
                    ProviderBudgetHoldSchedules.FormatReset(new(payload.BindingKey, payload.ResetAt, intent.Id)));
                var saved = scratch.ExportGoalSnapshot(hold.SourceGoalId);
                return Task.FromResult<(bool, GoalSnapshot?, (bool, GoalSnapshot?))>((true, saved, (false, saved)));
            }).GetAwaiter().GetResult();
        if (result.Snapshot is null) throw new InvalidOperationException("source-goal-not-stored");

        var retained = new AgentOrchestratorKernel(new FixedClock(now));
        retained.ReplaceGoalWithSnapshot(result.Snapshot);
        kernel.ProviderHoldHistory.Retain(retained.GetGoal(hold.SourceGoalId), kernel.Goals);
        return result.Replayed;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
