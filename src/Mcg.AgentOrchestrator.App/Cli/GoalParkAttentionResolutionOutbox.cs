using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalParkAttentionResolutionOutbox
{
    internal const string Kind = "goal-park-attention-resolution";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Payload(string GoalId, DateTimeOffset ParkedAt, string Resolution);

    internal static OrchestratorStateOutboxMessage CreateMessage(GoalId goalId, string resolution, DateTimeOffset parkedAt) =>
        new(Guid.NewGuid().ToString("N"), Kind,
            JsonSerializer.Serialize(new Payload(goalId.Value, parkedAt, resolution), JsonOptions), parkedAt);

    internal static async Task<OrchestratorStateOutboxProcessingResult> Process(
        ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace,
        OrchestratorStateOutboxMessage message, CancellationToken cancellationToken, Action<int>? onResolved = null)
    {
        Payload payload;
        try { payload = Deserialize(message); }
        catch (JsonException ex) { return OrchestratorStateOutboxProcessingResult.Quarantined(ex.Message); }

        var goal = await repository.LoadGoalAsync(new GoalId(payload.GoalId), cancellationToken);
        if (goal is null)
            return OrchestratorStateOutboxProcessingResult.Quarantined($"Park attention resolution targets a missing goal: {payload.GoalId}");
        if (goal.Status != GoalStatus.Parked)
            return new(OrchestratorStateOutboxDisposition.Complete, "goal no longer Parked");

        var count = await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ResolveOpenForGoalRaisedAtOrBeforeAsync(payload.GoalId, payload.Resolution, payload.ParkedAt, cancellationToken);
        onResolved?.Invoke(count);
        return OrchestratorStateOutboxProcessingResult.Completed;
    }

    private static Payload Deserialize(OrchestratorStateOutboxMessage message)
    {
        var payload = JsonSerializer.Deserialize<Payload>(message.PayloadJson, JsonOptions);
        if (message.Kind != Kind || payload is null || !Guid.TryParseExact(payload.GoalId, "N", out _) ||
            payload.ParkedAt == default || string.IsNullOrWhiteSpace(payload.Resolution))
            throw new JsonException("Invalid park attention resolution payload.");
        return payload;
    }
}
