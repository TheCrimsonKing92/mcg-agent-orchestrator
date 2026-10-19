using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionAgentReadView
{
    internal static string Render(IReadOnlyList<HumanInputRequest> waits,
        IReadOnlyList<CollaborationItem> items, TimeProvider? time = null,
        IReadOnlyList<ProviderUsageHoldRow>? providerHolds = null) =>
        AgentReadEnvelope.Serialize(AgentReadEnvelope.AttentionSchema, AgentReadEnvelope.AttentionSchemaVersion,
            new
            {
                waits = waits.Select(wait => new
                {
                    id = wait.Id.Value,
                    goalId = wait.GoalId.Value,
                    taskId = wait.TaskId?.Value,
                    kind = wait.Kind.ToString(),
                    question = wait.Question,
                    requestedAt = wait.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                    isAutoDefaultable = wait.IsAutoDefaultable,
                    isDismissible = wait.IsDismissible,
                    isAnswerRequired = wait.IsAnswerRequired,
                    isExternallyBlocked = wait.IsExternallyBlocked,
                    resumeCommand = wait.ResumeCommand
                }).ToArray(),
                items = items.Select(item => new
                {
                    id = item.Id,
                    type = item.Type.ToString(),
                    goalId = item.GoalId,
                    status = item.Status.ToString(),
                    subject = item.Subject,
                    body = item.Body,
                    correlationKey = item.CorrelationKey,
                    raisedAt = item.RaisedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray(),
                providerHolds = (providerHolds ?? []).Select(ProviderUsageHoldJson.Row).ToArray()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
}
