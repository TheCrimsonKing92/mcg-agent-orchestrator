using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public sealed record DashboardNextActionControl(
    string Label,
    string Method,
    string Url,
    string? CostRisk = null,
    string? CostRecommendation = null);

public static class DashboardNextActionControls
{
    public static DashboardNextActionControl? Build(
        Goal goal,
        NextActionItem item,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        var goalPrefix = goal.Id.Value[..8];
        int? taskNumber = item.TaskId is null ? null : GetTaskDisplayNumber(goal, item.TaskId);

        return item.Kind switch
        {
            NextActionKind.RunAssignedTask when taskNumber is not null =>
                new DashboardNextActionControl(
                    GetRunActionLabel(goal, item.TaskId, agents, agentDefinitions),
                    "POST",
                    BuildTaskRunUrl(goal, item.TaskId!, agents, agentDefinitions),
                    BuildRunAssignedCostRiskLabel(goal, item.TaskId!, workerProfiles, agents, agentDefinitions),
                    BuildRunAssignedCostRecommendation(goal, item.TaskId!, workerProfiles, agents, agentDefinitions)),
            NextActionKind.RefreshRunningProcess when taskNumber is not null =>
                new DashboardNextActionControl("Refresh process", "POST", $"/api/goals/{goalPrefix}/tasks/{taskNumber}/refresh"),
            NextActionKind.ExecuteRecordedDispatch when taskNumber is not null =>
                new DashboardNextActionControl(
                    "Start prepared work",
                    "POST",
                    $"/api/goals/{goalPrefix}/tasks/{taskNumber}/start?confirmDispatchStart=true",
                    BuildPreparedDispatchCostRiskLabel(goal, item.TaskId!),
                    BuildPreparedDispatchCostRecommendation(goal, item.TaskId!)),
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
        return TaskDisplayNumber.Resolve(goal, taskId);
    }

    public static string GetRunActionLabel(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        var policy = ResolveTaskExecutionPolicy(goal, taskId, agents, agentDefinitions);
        return policy switch
        {
            AgentExecutionPolicy.SubscriptionOnly or AgentExecutionPolicy.PreferSubscription => "Prepare subscription handoff",
            AgentExecutionPolicy.AnyAvailable => "Prepare subscription handoff",
            _ => RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: false, agents, agentDefinitions)
                ? "Run paid API task"
                : "Run task"
        };
    }

    public static string GetExplicitApiRunActionLabel(
        Goal goal,
        TaskId taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        return RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: true, agents, agentDefinitions)
            ? "Explicit paid API run"
            : "Explicit API run";
    }

    public static bool CanRunApiExplicitly(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        if (ResolveTaskExecutionPolicy(goal, taskId, agents, agentDefinitions) is not AgentExecutionPolicy.AnyAvailable || taskId is null)
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
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        var taskNumber = GetTaskDisplayNumber(goal, taskId);
        var url = $"/api/goals/{goal.Id.Value[..8]}/tasks/{taskNumber}/run?confirmTaskRun=true";
        return RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: false, agents, agentDefinitions)
            ? $"{url}&confirmPaidApiRun=true"
            : url;
    }

    public static string BuildExplicitApiRunUrl(
        Goal goal,
        TaskId taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents = null,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        var taskNumber = GetTaskDisplayNumber(goal, taskId);
        var url = $"/api/goals/{goal.Id.Value[..8]}/tasks/{taskNumber}/api-run?confirmTaskRun=true";
        return RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun: true, agents, agentDefinitions)
            ? $"{url}&confirmPaidApiRun=true"
            : url;
    }

    private static AgentExecutionPolicy? ResolveTaskExecutionPolicy(
        Goal goal,
        TaskId? taskId,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        if (taskId is null)
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return null;
        }

        return FindAgentDefinition(task, agentDefinitions)?.ExecutionPolicy
            ?? agents?.FirstOrDefault(agent => agent.Role == task.RequiredRole)?.ExecutionPolicy;
    }

    private static bool RequiresPaidApiRunConfirmation(
        Goal goal,
        TaskId? taskId,
        bool explicitApiRun,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions = null)
    {
        if (taskId is null)
        {
            return false;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return false;
        }

        if (FindAgentDefinition(task, agentDefinitions) is { } agentDefinition)
        {
            if (!explicitApiRun && agentDefinition.ExecutionPolicy != AgentExecutionPolicy.ApiOnly)
            {
                return false;
            }

            if (explicitApiRun &&
                (!AgentExecutionPolicies.AllowsApi(agentDefinition.ExecutionPolicy) ||
                    (agentDefinition.ExecutionPolicy != AgentExecutionPolicy.ApiOnly && !CanRunApiExplicitly(goal, taskId, agents, agentDefinitions))))
            {
                return false;
            }

            return TryPreviewApiRun(goal, task, agentDefinitions!) is { } preview &&
                ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName);
        }

        var agent = agents?.FirstOrDefault(candidate => candidate.Role == task.RequiredRole);
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

    private static bool RequiresLargePaidApiRunConfirmation(
        Goal goal,
        TaskId taskId,
        bool explicitApiRun,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (agentDefinitions is null)
        {
            return false;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return false;
        }

        var agent = FindAgentDefinition(task, agentDefinitions);
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
                (agent.ExecutionPolicy != AgentExecutionPolicy.ApiOnly && !CanRunApiExplicitly(goal, taskId, agents, agentDefinitions))))
        {
            return false;
        }

        return TryPreviewApiRun(goal, task, agentDefinitions) is { } preview &&
            ApiPromptCostGuard.Evaluate(preview, goal) is not null;
    }

    private static string? BuildApiCostRiskLabel(
        Goal goal,
        TaskId taskId,
        bool explicitApiRun,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (!RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun, agents, agentDefinitions))
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is not null &&
            agentDefinitions is not null &&
            TryPreviewApiRun(goal, task, agentDefinitions) is { } preview &&
            ApiPromptCostGuard.Evaluate(preview, goal) is { } risk)
        {
            return ApiPromptCostGuard.BuildInlineLabel(risk);
        }

        return "paid API";
    }

    private static string? BuildRunAssignedCostRiskLabel(
        Goal goal,
        TaskId taskId,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (TryResolveSubscriptionCostContext(goal, taskId, workerProfiles, agents, agentDefinitions) is { } context)
        {
            if (context.ModelFit?.UnderpoweredCount > 0)
            {
                return "prior underpowered subscription model";
            }

            if (context.ModelFit?.OverkillCount > 0)
            {
                return "prior overkill subscription model";
            }

            return "paid subscription handoff";
        }

        return BuildApiCostRiskLabel(goal, taskId, explicitApiRun: false, agents, agentDefinitions);
    }

    private static string? BuildRunAssignedCostRecommendation(
        Goal goal,
        TaskId taskId,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (TryResolveSubscriptionCostContext(goal, taskId, workerProfiles, agents, agentDefinitions) is { } context)
        {
            if (context.ModelFit?.UnderpoweredCount > 0)
            {
                return $"Prior evidence says {context.ProviderName}/{context.ModelName} was underpowered; choose a stronger model before paid subscription handoff.";
            }

            if (context.ModelFit?.OverkillCount > 0)
            {
                return CostRecommendationText.PaidStartOverkill(context.ProviderName, context.ModelName);
            }

            return $"Review subscription-plan first; {CostRecommendationText.LocalModelSwitchAction} before paid subscription handoff when the task is routine.";
        }

        return BuildApiCostRecommendation(goal, taskId, explicitApiRun: false, agents, agentDefinitions);
    }

    private static SubscriptionCostContext? TryResolveSubscriptionCostContext(
        Goal goal,
        TaskId taskId,
        WorkerProfileCatalog workerProfiles,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null)
        {
            return null;
        }

        if (FindAgentDefinition(task, agentDefinitions) is { } agentDefinition)
        {
            if (!AgentExecutionPolicies.AllowsSubscription(agentDefinition.ExecutionPolicy))
            {
                return null;
            }

            var profiles = workerProfiles;
            var templateVariables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(
                agentDefinition,
                goal,
                task,
                profiles,
                allowCheapLane: false);
            var providerName = GetTemplateValue(templateVariables, "providerName") ?? agentDefinition.Model.ProviderName;
            var modelName = GetTemplateValue(templateVariables, "subscriptionModelName") ??
                agentDefinition.Subscription?.ModelAlias ??
                agentDefinition.Model.ModelName;
            return ProviderSmokeRunner.IsPaidProviderName(providerName)
                ? new SubscriptionCostContext(providerName, modelName, FindModelFit(goal, providerName, modelName))
                : null;
        }

        var agent = agents?.FirstOrDefault(candidate => candidate.Role == task.RequiredRole);
        if (agent is null || !AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            return null;
        }

        var healthProvider = agent.ProviderName;
        var healthModel = agent.SubscriptionModelAlias ?? agent.ModelName;
        return ProviderSmokeRunner.IsPaidProviderName(healthProvider)
            ? new SubscriptionCostContext(healthProvider, healthModel, FindModelFit(goal, healthProvider, healthModel))
            : null;
    }

    private static string? GetTemplateValue(IReadOnlyDictionary<string, string?> variables, string name)
    {
        return variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static ModelFitSummary? FindModelFit(Goal goal, string providerName, string modelName)
    {
        return ModelFitEvidence
            .BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes))
            .FirstOrDefault(fit =>
                fit.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                fit.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildApiCostRecommendation(
        Goal goal,
        TaskId taskId,
        bool explicitApiRun,
        IReadOnlyList<AgentConfigurationValidation>? agents,
        IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (!RequiresPaidApiRunConfirmation(goal, taskId, explicitApiRun, agents, agentDefinitions))
        {
            return null;
        }

        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        if (task is null ||
            agentDefinitions is null ||
            TryPreviewApiRun(goal, task, agentDefinitions) is not { } preview ||
            ApiPromptCostGuard.Evaluate(preview, goal) is not { } risk)
        {
            return null;
        }

        return ApiPromptCostGuard.BuildRecommendation(risk);
    }

    private static string? BuildPreparedDispatchCostRiskLabel(Goal goal, TaskId taskId)
    {
        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        return task is not null && SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task) is { } risk
            ? SubscriptionPromptCostGuard.BuildInlineLabel(risk)
            : null;
    }

    private static string? BuildPreparedDispatchCostRecommendation(Goal goal, TaskId taskId)
    {
        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == taskId);
        return task is not null && SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task) is { } risk
            ? SubscriptionPromptCostGuard.BuildRecommendation(risk)
            : null;
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

    private static AgentDefinition? FindAgentDefinition(TaskSpec task, IReadOnlyList<AgentDefinition>? agentDefinitions)
    {
        if (agentDefinitions is null)
        {
            return null;
        }

        return agentDefinitions.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId)
            ?? agentDefinitions.FirstOrDefault(candidate => candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole);
    }

    private static AgentTaskRunPreview? TryPreviewApiRun(Goal goal, TaskSpec task, IReadOnlyList<AgentDefinition> agentDefinitions)
    {
        try
        {
            return AgentTaskRunner.PreviewRun(goal, task, agentDefinitions);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private sealed record SubscriptionCostContext(string ProviderName, string ModelName, ModelFitSummary? ModelFit);
}


