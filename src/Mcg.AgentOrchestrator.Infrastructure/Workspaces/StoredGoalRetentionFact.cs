using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record StoredGoalRetentionFact(string GoalId, GoalStatus Status, DateTimeOffset? UpdatedAt);
