using Mcg.AgentOrchestrator.App.Providers;
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
                new DashboardNextActionControl(GetRunActionLabel(goal, item.TaskId, agents), "POST", BuildTaskRunUrl(goal, item.TaskId!, agents)),
            NextActionKind.RefreshRunningProcess when taskNumber is not null =>
                new DashboardNextActionControl("Refresh process", "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/refresh"),
            NextActionKind.ExecuteRecordedDispatch when taskNumber is not null =>
                new DashboardNextActionControl("Start prepared work", "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/start?confirmDispatchStart=true"),
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

    public static string BuildTaskRunUrl(
        Goal goal,
        TaskId taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null)
    {
        var taskNumber = GetTaskDisplayNumber(goal, taskId);
        var suffix = RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: false, agents)
            ? "&confirmPaidApiRun=true"
            : string.Empty;
        return $"/api/goals/{goal.Id.Value[..8]}/tasks/{taskNumber}/run?confirmTaskRun=true{suffix}";
    }

    public static string BuildExplicitApiRunUrl(
        Goal goal,
        TaskId taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null)
    {
        var taskNumber = GetTaskDisplayNumber(goal, taskId);
        var suffix = RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: true, agents)
            ? "&confirmPaidApiRun=true"
            : string.Empty;
        return $"/api/goals/{goal.Id.Value[..8]}/tasks/{taskNumber}/api-run?confirmTaskRun=true{suffix}";
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

    private static bool RequiresPaidApiRunConfirmation(
        Goal goal,
        TaskId? taskId,
        bool explicitApiRun,
        IReadOnlyList<AgentConfigurationValidation>? agents)
    {
        if (taskId is null || agents is null)
        {
            return false;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return false;
        }

        var agent = agents.FirstOrDefault(candidate => candidate.Role == task.RequiredRole);
        if (agent is null)
        {
            return false;
        }

        if (!explicitApiRun && agent.ExecutionPolicy != AgentExecutionPolicy.ApiOnly)
        {
            return false;
        }

        if (explicitApiRun &&
            (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy) ||
                (agent.ExecutionPolicy != AgentExecutionPolicy.ApiOnly && !CanRunApiExplicitly(goal, taskId, agents))))
        {
            return false;
        }

        var providerName = ResolveApiProviderName(goal, task, agent);
        return !string.IsNullOrWhiteSpace(providerName) &&
            ProviderSmokeRunner.IsPaidProviderName(providerName);
    }

    private static string ResolveApiProviderName(Goal goal, TaskSpec task, AgentConfigurationValidation agent)
    {
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        if (complexity == TaskComplexity.Complex && !string.IsNullOrWhiteSpace(agent.ComplexProviderName))
        {
            return agent.ComplexProviderName;
        }

        return agent.ProviderName;
    }
}


