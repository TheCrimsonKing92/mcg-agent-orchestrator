namespace Mcg.AgentOrchestrator.Core;

public sealed class AgentTaskRunner
{
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

        var resolvedModel = TaskComplexityEstimator.ResolveModel(agent, TaskComplexity.Auto, task.Description, goal.Objective);
        var provider = _providers.GetRequired(resolvedModel.ProviderName);
        _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"{agent.Name} started task (model: {resolvedModel.ModelName}).");

        ModelResponse response;
        try
        {
            response = await provider.CompleteAsync(BuildRequest(goal, task, agent, resolvedModel), cancellationToken).ConfigureAwait(false);
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
            _clock.UtcNow);

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

    private static ModelRequest BuildRequest(Goal goal, TaskSpec task, AgentDefinition agent, ModelProfile resolvedModel)
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
            goal.Timeline
                .Where(evt => evt.TaskId == task.Id || evt.TaskId is null)
                .TakeLast(12)
                .Select(evt => PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: false)));

        var userPrompt =
            $"Goal: {goal.Objective}{Environment.NewLine}" +
            $"Task: {task.Description}{Environment.NewLine}" +
            $"Task role: {task.RequiredRole}{Environment.NewLine}" +
            $"Current task status: {task.Status}{Environment.NewLine}" +
            $"Verification plan: {FormatVerificationPlan(task.VerificationPlan)}{Environment.NewLine}" +
            $"Role requirements:{Environment.NewLine}{SdlcRolePromptRequirements.BuildPlainText(agent.Role)}{Environment.NewLine}" +
            $"Recent timeline:{Environment.NewLine}{timeline}";

        if (isLocal)
        {
            userPrompt = LocalModelPromptOptimizer.AppendThinkingGuidance(userPrompt, agent.Role);
        }

        return new ModelRequest(
            systemPrompt,
            [new ModelMessage("user", userPrompt)],
            new ModelOptions(Temperature: 0.2, MaxOutputTokens: resolvedModel.MaxOutputTokens ?? 1200, ReasoningEffort: resolvedModel.ReasoningEffort, ModelName: resolvedModel.ModelName));
    }

    private static string FormatVerificationPlan(string? verificationPlan)
    {
        return string.IsNullOrWhiteSpace(verificationPlan)
            ? "none"
            : PromptContextFormatter.TrimPromptBlock(verificationPlan);
    }

    private static string TrimForTimeline(string value)
    {
        const int maxLength = 240;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
