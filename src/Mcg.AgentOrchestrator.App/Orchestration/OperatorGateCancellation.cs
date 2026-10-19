using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class OperatorGateCancellation(
    ConductorParallelAcceptanceAttemptCoordinator solo,
    ConductorGroupedGateAttemptCoordinator grouped,
    ConductEventLogWriter events)
{
    internal void Apply(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent, DateTimeOffset now)
    {
        GateCancelOperatorIntentPayload? payload;
        try { payload = JsonSerializer.Deserialize<GateCancelOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options); }
        catch (JsonException) { throw new InvalidOperationException("invalid-payload"); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Reason))
            throw new InvalidOperationException("invalid-payload");
        var groupedRecords = grouped.ReadAll().Where(a => a.Members.Any(m => m.GoalId == goal.Id.Value)).ToArray();
        if (groupedRecords.Any(a => a.Detail?.EndsWith($"intent={intent.Id}", StringComparison.Ordinal) == true) ||
            solo.HasOperatorCancelMarker(goal.Id.Value, intent.Id)) return;

        var live = solo.GetCapacityReservingAttempts([goal.Id.Value]).Where(a => a.Kind == "gate")
            .Select(a => (a.AttemptId, Solo: (ConductorParallelAcceptanceAttempt?)a, Grouped: (ConductorGroupedGateAttempt?)null))
            .Concat(groupedRecords.Where(a => a.Kind is "cohort" or "train" && grouped.ReservesCapacity(a))
                .Select(a => (a.AttemptId, Solo: (ConductorParallelAcceptanceAttempt?)null, Grouped: (ConductorGroupedGateAttempt?)a)))
            .OrderBy(a => a.AttemptId, StringComparer.Ordinal).ToArray();
        if (live.Length == 0) throw new InvalidOperationException("no-live-gate-attempt");
        var selected = payload.AttemptId is null ? live : live.Where(a => a.AttemptId == payload.AttemptId).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("attempt-not-live");
        foreach (var attempt in selected)
        {
            if (attempt.Solo is { } single)
            {
                var result = solo.CancelForOperator(single, intent.Id);
                if (result == OperatorGateStopResult.NotCancellable) throw new InvalidOperationException("holder-not-cancellable");
                if (result != OperatorGateStopResult.Stopped) throw new InvalidOperationException("holder-stop-unproven");
            }
            else if (!grouped.CancelForOperator(attempt.Grouped!, intent.Id))
                throw new InvalidOperationException("holder-stop-unproven");
            var kind = attempt.Grouped?.Kind ?? "solo";
            var members = attempt.Grouped is { } group ? string.Join('+', group.Members.Select(m => m.GoalId)) : goal.Id.Value;
            events.AppendRequired("gate-cancel", goal.Id.Value,
                $"GATE_CANCELLED attempt={attempt.AttemptId} kind={kind} members={members} intent={intent.Id} reason=operator", now);
            if (attempt.Solo is not null && goal.Status == GoalStatus.Verifying &&
                goal.Tasks.All(task => task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
                kernel.ReconcileGoalAcceptanceVerified(goal.Id,
                    $"Operator gate-cancel intent {intent.Id} cancelled acceptance attempt {attempt.AttemptId}; goal returned to Verified without a verdict.");
        }
    }
}
