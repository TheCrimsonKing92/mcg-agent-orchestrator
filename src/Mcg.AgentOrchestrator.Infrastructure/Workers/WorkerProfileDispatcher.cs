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
        IReadOnlyDictionary<string, string?>? variables = null)
    {
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);
        var preparation = WorkerCommandTemplate.Prepare(
            brief,
            profile.Name,
            profile.CommandTemplate,
            promptRoot,
            AddWorkingDirectoryVariable(workingDirectory, variables));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(profile.Name, preparation.Command, workingDirectory, dispatchedAt));
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
        var agent = ResolveAssignedAgent(task, agents);
        var profile = ResolveSubscriptionProfile(agent, profiles);
        EnsureSubscriptionProfileCanExecuteTask(profile, task);
        EnsureSubscriptionRetryWindowHasPassed(task, dispatchedAt);
        return PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, dispatchedAt, BuildSubscriptionTemplateVariables(agent));
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

            var profile = ResolveSubscriptionProfile(selection.Agent, profiles);
            EnsureSubscriptionProfileCanExecuteTask(profile, selection.Task);
            results.Add(PrepareTask(
                kernel,
                goal,
                selection.Task,
                profile,
                promptRoot,
                workingDirectory,
                dispatchedAt,
                BuildSubscriptionTemplateVariables(selection.Agent)));
        }

        return results;
    }

    public static WorkerProfile ResolveSubscriptionProfile(TaskSpec task, IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
    {
        var agent = ResolveAssignedAgent(task, agents);
        return ResolveSubscriptionProfile(agent, profiles);
    }

    public static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, WorkerProfileCatalog profiles)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for API execution only.");
        }

        return profiles.GetRequired(ResolveSubscriptionProfileName(agent));
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Subscription?.WorkerProfileName))
        {
            return agent.Subscription.WorkerProfileName;
        }

        if (agent.Model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiSubscriptionProfileName;
        }

        if (agent.Model.ProviderName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return AnthropicSubscriptionProfileName;
        }

        throw new InvalidOperationException($"Provider '{agent.Model.ProviderName}' does not have a default subscription worker profile.");
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
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["providerName"] = agent.Model.ProviderName,
            ["apiModelName"] = agent.Model.ModelName,
            ["apiReasoningEffort"] = agent.Model.ReasoningEffort,
            ["subscriptionModelName"] = agent.Subscription?.ModelAlias ?? agent.Model.ModelName,
            ["subscriptionReasoningEffort"] = agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort,
            ["executionPolicy"] = agent.ExecutionPolicy.ToString()
        };
    }

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
