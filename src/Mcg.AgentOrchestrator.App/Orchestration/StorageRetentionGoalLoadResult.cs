namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record StorageRetentionGoalLoadIssue(string GoalId, string Reason, string? ExceptionType);

internal sealed record StorageRetentionGoalLoadResult(
    IReadOnlyCollection<StorageRetentionGoal> Goals,
    string? ListingFailureExceptionType,
    IReadOnlyCollection<StorageRetentionGoalLoadIssue> SnapshotFailures);
