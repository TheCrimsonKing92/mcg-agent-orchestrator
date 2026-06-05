namespace Mcg.AgentOrchestrator.Core;

public sealed record TaskAssignment(TaskId TaskId, AgentId AgentId, AgentRole Role);

public sealed record DelegationPlan(GoalId GoalId, IReadOnlyList<TaskAssignment> Assignments);
