namespace Mcg.AgentOrchestrator.Infrastructure;

// Instance-scoped observation at query, deserialization and projection boundaries.
public sealed record GoalSnapshotReadObservation(string Operation, string? GoalId, long MaterializedCharacters = 0);
