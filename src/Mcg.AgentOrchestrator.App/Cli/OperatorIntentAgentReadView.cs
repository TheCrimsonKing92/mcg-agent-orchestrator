using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OperatorIntentAgentReadView
{
    internal static string Render(OperatorIntentRecord intent, TimeProvider? time = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.OperatorIntentSchema,
            AgentReadEnvelope.OperatorIntentSchemaVersion, new
            {
                id = intent.Id,
                idempotencyKey = intent.IdempotencyKey,
                verb = intent.Verb,
                goalId = intent.GoalId,
                taskId = intent.TaskId,
                status = intent.Status.ToString(),
                actor = intent.Actor,
                actorKind = intent.ActorKind.ToString(),
                channel = intent.Channel,
                createdAt = intent.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                claimedAt = intent.ClaimedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                completedAt = intent.CompletedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                outcome = intent.Outcome
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
