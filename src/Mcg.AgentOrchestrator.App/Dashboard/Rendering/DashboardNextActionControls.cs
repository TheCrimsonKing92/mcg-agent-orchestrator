using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public sealed record DashboardNextActionControl(string Label, string Method, string Url);

public static class DashboardNextActionControls
{
    public static DashboardNextActionControl? Build(
        Goal goal,
        NextActionItem item,
        IReadOnlyList<AgentConfigurationValidation>? agents = null)
    {
        var goalPrefix = goal.Id.Value[..8];
        int? taskNumber = item.TaskId is null ? null : GetTaskDisplayNumber(goal, item.TaskId);

        return item.Kind switch
        {
            NextActionKind.RunAssignedTask when taskNumber is not null =>
                new DashboardNextActionControl(GetRunActionLabel(goal, item.TaskId, agents), "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/run"),
            NextActionKind.RefreshRunningProcess when taskNumber is not null =>
                new DashboardNextActionControl("Refresh process", "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/refresh"),
            NextActionKind.ExecuteRecordedDispatch when taskNumber is not null =>
                new DashboardNextActionControl("Start prepared work", "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/start"),
            NextActionKind.DelegatePendingTask =>
                new DashboardNextActionControl("Assign tasks", "POST", $"/api/goals/{goalPrefix}/delegate"),
            NextActionKind.InspectFailedTask when taskNumber is not null =>
                new DashboardNextActionControl("Inspect task", "GET", $"/api/task/{taskNumber}?goal={goalPrefix}"),
            NextActionKind.FixFailedVerification when taskNumber is not null =>
                new DashboardNextActionControl("Verification records", "GET", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/verifications"),
            NextActionKind.MonitorGoal =>
                new DashboardNextActionControl("Monitor goal", "GET", $"/api/monitor?goal={goalPrefix}"),
            _ => null
        };
    }

    private static int GetTaskDisplayNumber(Goal goal, TaskId taskId)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == taskId)
            {
                return index + 1;
            }
        }

        throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    public static string GetRunActionLabel(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null)
    {
        var policy = ResolveTaskExecutionPolicy(goal, taskId, agents);
        return policy switch
        {
            AgentExecutionPolicy.SubscriptionOnly or AgentExecutionPolicy.PreferSubscription => "Prepare subscription handoff",
            AgentExecutionPolicy.AnyAvailable => "Prepare subscription handoff",
            _ => "Run task"
        };
    }

    public static bool CanRunApiExplicitly(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null)
    {
        if (ResolveTaskExecutionPolicy(goal, taskId, agents) is not AgentExecutionPolicy.AnyAvailable || taskId is null)
        {
            return false;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        return task is not null &&
            task.Status == WorkTaskStatus.Assigned &&
            task.LastDispatch is null &&
            task.LastProcess is null &&
            task.LastExecution is null &&
            task.LastVerification is null &&
            task.SubscriptionRetryAfter is null;
    }

    private static AgentExecutionPolicy? ResolveTaskExecutionPolicy(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents)
    {
        if (taskId is null || agents is null)
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        return task is null
            ? null
            : agents.FirstOrDefault(agent => agent.Role == task.RequiredRole)?.ExecutionPolicy;
    }
}


