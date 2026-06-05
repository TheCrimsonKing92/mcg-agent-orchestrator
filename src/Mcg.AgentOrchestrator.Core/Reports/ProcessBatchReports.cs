namespace Mcg.AgentOrchestrator.Core;

public sealed record ProcessBatchPlan(
    GoalId GoalId,
    string Objective,
    GoalStatus Status,
    ProcessBatchActionKind Action,
    int ReadyCount,
    int SkippedCount,
    IReadOnlyList<ProcessBatchPlanItem> Items);

public sealed record ProcessBatchPlanItem(
    TaskId TaskId,
    AgentRole Role,
    string Description,
    WorkTaskStatus TaskStatus,
    ProcessBatchItemStatus Status,
    string Reason);
