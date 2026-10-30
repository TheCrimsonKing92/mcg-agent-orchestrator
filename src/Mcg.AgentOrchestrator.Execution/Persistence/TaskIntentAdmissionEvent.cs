namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TaskIntentAdmissionEvent(
    string Kind, string GoalId, string TaskId, long PreparedVersion,
    DateTimeOffset? DispatchedAt, string IntentId, string IntentStatus,
    string? Outcome, double WaitedMilliseconds);
