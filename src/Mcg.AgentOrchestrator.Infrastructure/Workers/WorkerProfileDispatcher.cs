using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProfileDispatchResult(TaskSpec Task, string PromptPath);

public static class WorkerProfileDispatcher
{
    public const string OpenAiSubscriptionProfileName = "codex-cli";
    public const string AnthropicSubscriptionProfileName = "claude-cli";

    public static WorkerProfileDispatchResult PrepareTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        IReadOnlyDictionary<string, string?>? variables = null,
        string? providerName = null,
        string? modelName = null,
        string? reasoningEffort = null,
        TaskComplexity? taskComplexity = null)
    {
        EnsureTaskNeedsExecution(task);

        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);
        var preparation = WorkerCommandTemplate.Prepare(
            brief,
            profile.Name,
            profile.CommandTemplate,
            promptRoot,
            AddWorkingDirectoryVariable(workingDirectory, variables));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            profile.Name,
            preparation.Command,
            workingDirectory,
            dispatchedAt,
            providerName,
            modelName,
            reasoningEffort,
            taskComplexity,
            preparation.PromptCharacterCount));
        return new WorkerProfileDispatchResult(task, preparation.PromptPath);
    }

    public static IReadOnlyList<WorkerProfileDispatchResult> PrepareReadyTasks(
        AgentOrchestratorKernel kernel,
        Goal goal,
        WorkerProfile profile,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt)
    {
        var results = new List<WorkerProfileDispatchResult>();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned).ToList())
        {
            results.Add(PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, dispatchedAt));
        }

        return results;
    }

    public static WorkerProfileDispatchResult PrepareSubscriptionTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt)
    {
        EnsureTaskNeedsExecution(task);

        var agent = ResolveAssignedAgent(task, agents);
        var selection = ResolveSubscriptionModel(agent, goal, task);
        var profile = ResolveSubscriptionProfile(agent, selection.Model, profiles);
        EnsureSubscriptionProfileCanExecuteTask(profile, task);
        EnsureSubscriptionRetryWindowHasPassed(task, dispatchedAt);
        return PrepareTask(
            kernel,
            goal,
            task,
            profile,
            promptRoot,
            workingDirectory,
            dispatchedAt,
            BuildSubscriptionTemplateVariables(agent, selection),
            selection.Model.ProviderName,
            ResolveEffectiveSubscriptionModelName(agent, selection),
            ResolveEffectiveSubscriptionReasoningEffort(agent, selection),
            selection.Complexity);
    }

    public static IReadOnlyList<WorkerProfileDispatchResult> PrepareSubscriptionReadyTasks(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt)
    {
        var selections = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Assigned)
            .Select(task => new
            {
                Task = task,
                Agent = ResolveAssignedAgent(task, agents)
            })
            .ToList();

        var results = new List<WorkerProfileDispatchResult>();
        foreach (var selection in selections)
        {
            if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(selection.Task, dispatchedAt, out _))
            {
                continue;
            }

            var subscriptionModel = ResolveSubscriptionModel(selection.Agent, goal, selection.Task);
            var profile = ResolveSubscriptionProfile(selection.Agent, subscriptionModel.Model, profiles);
            EnsureSubscriptionProfileCanExecuteTask(profile, selection.Task);
            results.Add(PrepareTask(
                kernel,
                goal,
                selection.Task,
                profile,
                promptRoot,
                workingDirectory,
                dispatchedAt,
                BuildSubscriptionTemplateVariables(selection.Agent, subscriptionModel),
                subscriptionModel.Model.ProviderName,
                ResolveEffectiveSubscriptionModelName(selection.Agent, subscriptionModel),
                ResolveEffectiveSubscriptionReasoningEffort(selection.Agent, subscriptionModel),
                subscriptionModel.Complexity));
        }

        return results;
    }

    private static void EnsureTaskNeedsExecution(TaskSpec task)
    {
        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{task.Id}' already has passing verification; retry the task before dispatching it again.");
        }

        if (task.Status != WorkTaskStatus.Assigned)
        {
            throw new InvalidOperationException($"Task '{task.Id}' status is {task.Status}; retry or assign it before dispatching it again.");
        }
    }

    public static WorkerProfile ResolveSubscriptionProfile(TaskSpec task, IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
    {
        var agent = ResolveAssignedAgent(task, agents);
        return ResolveSubscriptionProfile(agent, profiles);
    }

    public static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, WorkerProfileCatalog profiles)
    {
        return ResolveSubscriptionProfile(agent, agent.Model, profiles);
    }

    private static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, ModelProfile model, WorkerProfileCatalog profiles)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for API execution only.");
        }

        return profiles.GetRequired(ResolveSubscriptionProfileName(agent, model));
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent)
    {
        return ResolveSubscriptionProfileName(agent, agent.Model);
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent, Goal goal, TaskSpec task)
    {
        return ResolveSubscriptionProfileName(agent, ResolveSubscriptionModel(agent, goal, task).Model);
    }

    private static string ResolveSubscriptionProfileName(AgentDefinition agent, ModelProfile model)
    {
        if (!string.IsNullOrWhiteSpace(agent.Subscription?.WorkerProfileName))
        {
            return agent.Subscription.WorkerProfileName;
        }

        if (model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiSubscriptionProfileName;
        }

        if (model.ProviderName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return AnthropicSubscriptionProfileName;
        }

        throw new InvalidOperationException($"Provider '{model.ProviderName}' does not have a default subscription worker profile.");
    }

    private static void EnsureSubscriptionRetryWindowHasPassed(TaskSpec task, DateTimeOffset dispatchedAt)
    {
        if (!DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, dispatchedAt, out var retryAfter))
        {
            return;
        }

        throw new InvalidOperationException($"Task '{task.Id}' hit a recoverable subscription usage limit; retry after {retryAfter:u}.");
    }

    public static IReadOnlyDictionary<string, string?> BuildSubscriptionTemplateVariables(AgentDefinition agent)
    {
        return BuildSubscriptionTemplateVariables(agent, new SubscriptionModelSelection(TaskComplexity.Simple, agent.Model, UsesComplexModel: false));
    }

    public static IReadOnlyDictionary<string, string?> BuildSubscriptionTemplateVariables(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task)
    {
        return BuildSubscriptionTemplateVariables(agent, ResolveSubscriptionModel(agent, goal, task));
    }

    private static Dictionary<string, string?> BuildSubscriptionTemplateVariables(
        AgentDefinition agent,
        SubscriptionModelSelection selection)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["providerName"] = selection.Model.ProviderName,
            ["apiModelName"] = selection.Model.ModelName,
            ["apiReasoningEffort"] = selection.Model.ReasoningEffort,
            ["subscriptionModelName"] = ResolveEffectiveSubscriptionModelName(agent, selection),
            ["subscriptionReasoningEffort"] = ResolveEffectiveSubscriptionReasoningEffort(agent, selection),
            ["taskComplexity"] = selection.Complexity.ToString(),
            ["executionPolicy"] = agent.ExecutionPolicy.ToString()
        };
    }

    private static string ResolveEffectiveSubscriptionModelName(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesComplexModel
            ? selection.Model.ModelName
            : agent.Subscription?.ModelAlias ?? selection.Model.ModelName;
    }

    private static string? ResolveEffectiveSubscriptionReasoningEffort(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesComplexModel
            ? selection.Model.ReasoningEffort ?? agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort
            : agent.Subscription?.ReasoningEffort ?? selection.Model.ReasoningEffort;
    }

    private static SubscriptionModelSelection ResolveSubscriptionModel(AgentDefinition agent, Goal goal, TaskSpec task)
    {
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var model = TaskComplexityEstimator.ResolveModel(agent, complexity, task.Description, goal.Objective);
        return new SubscriptionModelSelection(complexity, model, complexity == TaskComplexity.Complex && agent.ComplexModel is not null);
    }

    private sealed record SubscriptionModelSelection(TaskComplexity Complexity, ModelProfile Model, bool UsesComplexModel);

    private static AgentDefinition ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        if (task.AssignedAgentId is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
        }

        return agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
    }

    private static void EnsureRealSubscriptionProfile(WorkerProfile profile)
    {
        if (!WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' only echoes the prompt path; configure a real launcher before dispatch.");
    }

    private static void EnsureSubscriptionProfileCanExecuteTask(WorkerProfile profile, TaskSpec task)
    {
        EnsureRealSubscriptionProfile(profile);

        if (task.RequiredRole != AgentRole.Developer)
        {
            return;
        }

        var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
        if (capability.IsPatchCapable)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' is not patch-capable for Developer tasks: {capability.Detail}");
    }

    private static Dictionary<string, string?> AddWorkingDirectoryVariable(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? variables)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingDirectory"] = workingDirectory
        };

        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
