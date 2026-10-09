namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerAttentionObservation(OwnerQuestion Question, DateTimeOffset FirstSeen,
    DateTimeOffset? ResolvedAt = null);
