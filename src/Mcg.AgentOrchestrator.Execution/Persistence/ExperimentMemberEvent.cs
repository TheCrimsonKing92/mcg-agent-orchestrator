namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ExperimentMemberEvent(string EventId, string PairId, string Arm, string GoalId,
    string BriefSha256, string Reason, DateTimeOffset AddedAt);
