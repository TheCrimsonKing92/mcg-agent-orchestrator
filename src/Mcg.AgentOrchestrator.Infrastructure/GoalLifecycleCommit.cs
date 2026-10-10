using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Structural fields of a committed goal-events line; message text stays in the source log.
public sealed record GoalLifecycleCommit(
    GoalId GoalId,
    string EventType,
    DateTimeOffset Timestamp,
    string? TaskId,
    string? Role,
    bool HasOperatorIntent);
