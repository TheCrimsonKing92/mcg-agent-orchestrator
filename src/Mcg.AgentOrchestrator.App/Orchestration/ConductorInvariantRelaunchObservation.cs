namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorFinishedAttempt(string AttemptId, string GoalId, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

internal sealed record ConductorRelaunchObservation(DateTimeOffset? ScheduledAt, string? GoalId,
    DateTimeOffset? LastLiveGateAt, DateTimeOffset? FailureOutcomeAt, string? RunningBuildCommit,
    DateTimeOffset? HandoffAt = null)
{
    internal static ConductorRelaunchObservation Empty { get; } = new(null, null, null, null, null);
}
