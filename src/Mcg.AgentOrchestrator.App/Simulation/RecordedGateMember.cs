namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record RecordedGateMember(string GoalId, string CandidateRevision,
    IReadOnlyList<string> LandingPaths, IReadOnlyList<string> ResourceKeys);
