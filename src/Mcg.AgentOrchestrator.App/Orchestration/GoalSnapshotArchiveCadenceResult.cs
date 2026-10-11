namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalSnapshotArchiveCadenceResult(
    DateTimeOffset Cutoff,
    int Archived,
    string Status,
    string? ExceptionType);
