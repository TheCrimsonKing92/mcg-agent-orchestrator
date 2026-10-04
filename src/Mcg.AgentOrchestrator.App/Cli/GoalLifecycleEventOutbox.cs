using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalLifecycleEventOutbox
{
    internal const string Kind = "goal-lifecycle-event";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Payload(string GoalId, string EventKind, ProgressEvent Event);

    internal static OrchestratorStateOutboxMessage CreateMessage(ProgressEvent progressEvent) =>
        new(Guid.NewGuid().ToString("N"), Kind,
            JsonSerializer.Serialize(new Payload(progressEvent.GoalId.Value,
                nameof(ProgressKind.GoalCancelled), progressEvent), JsonOptions), DateTimeOffset.UtcNow);

    internal static void AppendForMessage(OrchestratorWorkspace workspace, OrchestratorStateOutboxMessage message)
    {
        var payload = Deserialize(message);
        new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory)
            .AppendIdempotent(new GoalId(payload.GoalId), message.Id,
                writer => writer.AppendTimelineEvent(payload.Event));
    }

    internal static async Task<OrchestratorStateOutboxProcessingResult> Process(
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        OrchestratorStateOutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = Deserialize(message);
            if (await repository.LoadGoalAsync(new GoalId(payload.GoalId), cancellationToken) is null)
                return OrchestratorStateOutboxProcessingResult.Quarantined(
                    $"Goal lifecycle event targets a missing goal: {payload.GoalId}");

            AppendForMessage(workspace, message);
            return OrchestratorStateOutboxProcessingResult.Completed;
        }
        catch (JsonException ex)
        {
            return OrchestratorStateOutboxProcessingResult.Quarantined(
                $"Invalid goal lifecycle event payload: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return OrchestratorStateOutboxProcessingResult.Quarantined(
                $"Goal lifecycle event contains an invalid goal id: {ex.Message}");
        }
    }

    private static Payload Deserialize(OrchestratorStateOutboxMessage message)
    {
        var payload = JsonSerializer.Deserialize<Payload>(message.PayloadJson, JsonOptions)
            ?? throw new JsonException("Goal lifecycle event payload is null.");
        if (!Guid.TryParseExact(payload.GoalId, "N", out _))
            throw new ArgumentException("Goal id must be a GUID in N format.");
        if (message.Kind != Kind || payload.EventKind != nameof(ProgressKind.GoalCancelled) ||
            payload.Event is null || payload.Event.Kind != ProgressKind.GoalCancelled ||
            payload.Event.GoalId?.Value != payload.GoalId)
            throw new JsonException("Goal lifecycle event payload does not describe a matching GoalCancelled event.");
        return payload;
    }
}
