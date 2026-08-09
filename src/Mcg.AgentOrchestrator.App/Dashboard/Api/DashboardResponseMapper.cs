using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class DashboardResponseMapper
{
public static GoalSummaryDto ToGoalSummary(Goal goal, string? executionDirectory = null)
{
    var lifecycle = ResolveLifecycle(goal, executionDirectory);
    return new GoalSummaryDto(
        goal.Id.Value,
        OutputTextPreview.CreateSummary(goal.Objective).Text,
        EffectiveStatus(goal.Status, lifecycle),
        goal.Tasks.Count,
        goal.Timeline.LastOrDefault()?.OccurredAt)
    {
        StatusText = GoalStatusText(goal.Status, lifecycle),
        Condition = GoalLifecycle.HasActiveFailedTask(goal)
            ? GoalLifecycle.ActiveWithFailedTaskCondition
            : null
    };
}

public static GoalDetailDto ToGoalDetailDto(
    AgentOrchestratorKernel kernel,
    Goal goal,
    string? executionDirectory = null)
{
    return new GoalDetailDto(
        ToGoalSummary(goal, executionDirectory),
        goal.Tasks.Select(task => ToTaskSummaryDto(goal, task)).ToList(),
        kernel.BuildVerificationGate(goal.Id).IsSatisfied,
        MonitoringStreamPath: DashboardMonitoringEvents.StreamPath(goal.Id.Value));
}

public static DelegationPlanDto ToDelegationPlanDto(DelegationPlan plan)
{
    return new DelegationPlanDto(
        plan.GoalId.Value,
        plan.Assignments.Select(assignment => new TaskAssignmentDto(assignment.TaskId.Value, assignment.AgentId.Value, assignment.Role)).ToList());
}
}
