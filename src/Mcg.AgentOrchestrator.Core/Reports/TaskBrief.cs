namespace Mcg.AgentOrchestrator.Core;

public sealed record TaskBrief(
    GoalId GoalId,
    TaskId TaskId,
    AgentRole Role,
    string Title,
    string Content);
