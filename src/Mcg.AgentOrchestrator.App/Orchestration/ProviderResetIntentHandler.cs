using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProviderResetIntentHandler
{
    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent, DateTimeOffset now)
    {
        if (kernel.Goals.SelectMany(item => item.Timeline).Any(evt =>
                ProviderBudgetHoldSchedules.TryParseReset(evt, out var marker) && marker.IntentId == intent.Id)) return;
        var payload = JsonSerializer.Deserialize<ProviderResetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
            ?? throw new InvalidOperationException("provider-reset requires a payload.");
        if (!ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(kernel.Goals, payload.BindingKey, out _))
            throw new InvalidOperationException("no-current-hold");
        if (payload.ResetAt > now.AddDays(31)) throw new InvalidOperationException("reset-too-far");
        if (intent.GoalId != goal.Id.Value || intent.TaskId is null || !goal.Tasks.Any(task => task.Id.Value == intent.TaskId))
            throw new InvalidOperationException("provider-reset requires its source task in the walked goal.");
        kernel.RecordTaskNote(goal.Id, new TaskId(intent.TaskId), ProviderBudgetHoldSchedules.FormatReset(
            new(payload.BindingKey, payload.ResetAt, intent.Id)));
    }
}
