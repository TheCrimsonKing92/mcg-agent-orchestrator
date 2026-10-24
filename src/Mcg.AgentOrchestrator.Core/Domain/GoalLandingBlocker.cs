namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalLandingBlocker(
    string Kind, string Detail, string Identity, DateTimeOffset Since, string NextActor, string NextVerb);
