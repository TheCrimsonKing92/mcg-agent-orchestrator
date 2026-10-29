namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayStoredAttempt(
    string AttemptId, string GoalId, DateTimeOffset StartedAt,
    string MainHeadSha, string BranchHeadSha, string GoalDirectory,
    IReadOnlyList<string> TestResultPaths, IReadOnlyList<string> FailedChecks);
