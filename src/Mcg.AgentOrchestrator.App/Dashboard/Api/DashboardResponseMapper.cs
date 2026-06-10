using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static GoalSummaryDto ToGoalSummary(Goal goal)
{
    return new GoalSummaryDto(
        goal.Id.Value,
        OutputTextPreview.CreateSummary(goal.Objective).Text,
        goal.Status,
        goal.Tasks.Count,
        goal.Timeline.LastOrDefault()?.OccurredAt);
}

public static GoalDetailDto ToGoalDetailDto(AgentOrchestratorKernel kernel, Goal goal)
{
    return new GoalDetailDto(
        ToGoalSummary(goal),
        goal.Tasks.Select(task => ToTaskSummaryDto(goal, task)).ToList(),
        kernel.BuildVerificationGate(goal.Id).IsSatisfied);
}

public static DelegationPlanDto ToDelegationPlanDto(DelegationPlan plan)
{
    return new DelegationPlanDto(
        plan.GoalId.Value,
        plan.Assignments.Select(assignment => new TaskAssignmentDto(assignment.TaskId.Value, assignment.AgentId.Value, assignment.Role)).ToList());
}
}
