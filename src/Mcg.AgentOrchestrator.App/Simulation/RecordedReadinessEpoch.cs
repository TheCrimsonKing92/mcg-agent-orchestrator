namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record RecordedReadinessEpoch(string GoalId, DateTimeOffset ReadyAt,
    DateTimeOffset? LeaveAt, string CandidateRevision, IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys)
{
    internal RecordedGateMember Member => new(GoalId, CandidateRevision, LandingPaths, ResourceKeys);
}
