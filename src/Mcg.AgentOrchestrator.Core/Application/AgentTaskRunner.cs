namespace Mcg.AgentOrchestrator.Core;

public sealed class AgentTaskRunner
{
    private const int RoutinePaidProviderFallbackMaxOutputTokens = 768;
    private const int ComplexPaidProviderFallbackMaxOutputTokens = 1200;
    private const int RoutineLocalProviderFallbackMaxOutputTokens = 2048;
    private const int ComplexLocalProviderFallbackMaxOutputTokens = 8192;

    private readonly AgentOrchestratorKernel _kernel;
    private readonly IReadOnlyList<AgentDefinition> _agents;
    private readonly IModelProviderRegistry _providers;
    private readonly IClock _clock;

    public AgentTaskRunner(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        IClock? clock = null)
    {
        _kernel = kernel;
        _agents = agents;
        _providers = providers;
        _clock = clock ?? new SystemClock();
    }

    public async Task<AgentTaskRunResult> RunAsync(GoalId goalId, TaskId taskId, CancellationToken cancellationToken = default)
    {
        var goal = _kernel.GetGoal(goalId);
        var task = goal.FindTask(taskId);

        if (task.Status == WorkTaskStatus.WaitingForHuman)
        {
            throw new InvalidOperationException($"Task '{taskId}' is waiting for human input.");
        }

        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has passing verification; retry the task before running it again.");
        }

        if (task.LastExecution is not null)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has model output; verify it or retry the task before running it again.");
        }

        if (task.Status != WorkTaskStatus.Assigned)
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; retry or assign it before running it again.");
        }

        if (task.LastDispatch is not null || task.LastProcess is not null)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has dispatch evidence; retry the task before running it with a model provider.");
        }

        if (task.AssignedAgentId is null)
        {
            throw new InvalidOperationException($"Task '{taskId}' is not assigned to an agent.");
        }

        var agent = _agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId)
            ?? _agents.FirstOrDefault(candidate => candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
        if (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for subscription execution only.");
        }

        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var resolvedModel = TaskComplexityEstimator.ResolveModel(agent, complexity, task.Description, goal.Objective);
        var provider = _providers.GetRequired(resolvedModel.ProviderName);
        _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"{agent.Name} started task (model: {resolvedModel.ModelName}).");

        var request = BuildRequest(goal, task, agent, resolvedModel, complexity);
        ModelResponse response;
        try
        {
            response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, $"Model execution failed: {ex.Message}");
            throw;
        }

        var execution = new TaskExecutionRecord(
            agent.Id,
            agent.Name,
            resolvedModel.ProviderName,
            resolvedModel.ModelName,
            response.Text.Trim(),
            response.StopReason,
            response.Usage,
            _clock.UtcNow,
            complexity,
            request.Options.MaxOutputTokens);

        task.RecordExecution(execution);
        goal.Append(new ProgressEvent(goal.Id, task.Id, ProgressKind.TaskOutputRecorded, TrimForTimeline(execution.Output), execution.CompletedAt));

        var humanInputQuestion = AgentOutputDirectives.TryParseHumanInputRequest(execution.Output);
        if (humanInputQuestion is not null)
        {
            _kernel.RequestHumanInput(goal.Id, task.Id, humanInputQuestion);
            return new AgentTaskRunResult(goal, task, execution);
        }

        _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{agent.Name} completed task.");

        return new AgentTaskRunResult(goal, task, execution);
    }

    private static ModelRequest BuildRequest(Goal goal, TaskSpec task, AgentDefinition agent, ModelProfile resolvedModel, TaskComplexity complexity)
    {
        var isLocal = LocalModelPromptOptimizer.IsLocalProvider(resolvedModel.ProviderName);

        var baseSystemPrompt =
            $"You are the {agent.Role} agent in a software-development orchestrator. " +
            "Complete the assigned SDLC task, state concrete results, verification evidence, blockers, and any human input needed. " +
            "If you cannot proceed without operator input, include a line that starts with HUMAN_INPUT: followed by the exact question. " +
            "Avoid generic status summaries; ground conclusions in files, command output, or cited source material.";

        var systemPrompt = isLocal
            ? LocalModelPromptOptimizer.OptimizeSystemPrompt(baseSystemPrompt, agent.Role)
            : baseSystemPrompt;

        var timeline = string.Join(
            Environment.NewLine,
            PromptContextFormatter.SelectPromptTimelineEvents(
                    goal.Timeline.Where(evt => evt.TaskId == task.Id || evt.TaskId is null),
                    maxEvents: TimelineEventBudget(complexity),
                    complexity)
                .Select(evt => PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: false, complexity)));
        var responseBudgetGuidance = PromptContextFormatter.BuildResponseBudgetGuidance(complexity);
        var responseGuidance = string.IsNullOrWhiteSpace(responseBudgetGuidance)
            ? string.Empty
            : $"Response guidance: {responseBudgetGuidance}{Environment.NewLine}";
        var timelineSection = string.IsNullOrWhiteSpace(timeline)
            ? string.Empty
            : $"{Environment.NewLine}Recent timeline:{Environment.NewLine}{timeline}";

        var userPrompt =
            $"Goal: {PromptContextFormatter.TrimPrimaryContextBlock(goal.Objective, complexity)}{Environment.NewLine}" +
            $"Task: {PromptContextFormatter.TrimPrimaryContextBlock(task.Description, complexity)}{Environment.NewLine}" +
            $"Task role: {task.RequiredRole}{Environment.NewLine}" +
            $"Current task status: {task.Status}{Environment.NewLine}" +
            $"Verification plan: {FormatVerificationPlan(task.VerificationPlan, complexity)}{Environment.NewLine}" +
            responseGuidance +
            $"Role requirements:{Environment.NewLine}{SdlcRolePromptRequirements.BuildPlainText(agent.Role, complexity)}" +
            timelineSection;

        if (isLocal)
        {
            userPrompt = LocalModelPromptOptimizer.AppendThinkingGuidance(userPrompt, agent.Role);
        }

        return new ModelRequest(
            systemPrompt,
            [new ModelMessage("user", userPrompt)],
            new ModelOptions(
                Temperature: 0.2,
                MaxOutputTokens: ResolveMaxOutputTokens(resolvedModel, complexity),
                ReasoningEffort: resolvedModel.ReasoningEffort,
                ModelName: resolvedModel.ModelName));
    }

    private static int ResolveMaxOutputTokens(ModelProfile resolvedModel, TaskComplexity complexity)
    {
        if (resolvedModel.MaxOutputTokens is { } maxOutputTokens)
        {
            return maxOutputTokens;
        }

        if (LocalModelPromptOptimizer.IsLocalProvider(resolvedModel.ProviderName))
        {
            return complexity == TaskComplexity.Complex
                ? ComplexLocalProviderFallbackMaxOutputTokens
                : RoutineLocalProviderFallbackMaxOutputTokens;
        }

        return complexity == TaskComplexity.Complex
            ? ComplexPaidProviderFallbackMaxOutputTokens
            : RoutinePaidProviderFallbackMaxOutputTokens;
    }

    private static string FormatVerificationPlan(string? verificationPlan, TaskComplexity complexity)
    {
        return string.IsNullOrWhiteSpace(verificationPlan)
            ? "none"
            : PromptContextFormatter.TrimVerificationPlanBlock(verificationPlan, complexity);
    }

    private static int TimelineEventBudget(TaskComplexity complexity)
    {
        return complexity == TaskComplexity.Complex ? 12 : 6;
    }

    private static string TrimForTimeline(string value)
    {
        const int maxLength = 240;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
