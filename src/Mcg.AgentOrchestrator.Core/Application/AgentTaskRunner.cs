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

        var agent = ResolveAgent(task, _agents);
        if (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for subscription execution only.");
        }

        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var resolvedModel = TaskComplexityEstimator.ResolveModel(
            agent,
            complexity,
            task.Description,
            goal.Objective,
            BuildModelFitSummary(goal));
        var provider = _providers.GetRequired(resolvedModel.ProviderName);
        var startMessage = BuildStartMessage(agent, resolvedModel);
        var request = BuildRequest(
            goal,
            task,
            agent,
            resolvedModel,
            complexity,
            [new ProgressEvent(goal.Id, task.Id, ProgressKind.TaskStarted, startMessage, _clock.UtcNow)]);
        _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, startMessage);

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
            request.Options.MaxOutputTokens,
            CountPromptCharacters(request));

        task.RecordExecution(execution);
        goal.Append(new ProgressEvent(goal.Id, task.Id, ProgressKind.TaskOutputRecorded, TrimForTimeline(execution.Output), execution.CompletedAt));

        var humanInputQuestion = AgentOutputDirectives.TryParseHumanInputRequest(execution.Output);
        if (humanInputQuestion is not null)
        {
            _kernel.RequestHumanInput(goal.Id, task.Id, humanInputQuestion);
            return new AgentTaskRunResult(goal, task, execution);
        }

        if (HasOutputTokenLimitHit(execution))
        {
            _kernel.ReportTaskProgress(
                goal.Id,
                task.Id,
                WorkTaskStatus.Failed,
                $"{agent.Name} output may be truncated at {execution.MaxOutputTokens} token(s); retry with narrower scope or stronger model before accepting.");
            return new AgentTaskRunResult(goal, task, execution);
        }

        _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, $"{agent.Name} completed task.");

        return new AgentTaskRunResult(goal, task, execution);
    }

    public static AgentTaskRunPreview PreviewRun(Goal goal, TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        var agent = ResolveAgent(task, agents);
        if (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for subscription execution only.");
        }

        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var resolvedModel = TaskComplexityEstimator.ResolveModel(
            agent,
            complexity,
            task.Description,
            goal.Objective,
            BuildModelFitSummary(goal));
        var request = BuildRequest(
            goal,
            task,
            agent,
            resolvedModel,
            complexity,
            [new ProgressEvent(goal.Id, task.Id, ProgressKind.TaskStarted, BuildStartMessage(agent, resolvedModel), DateTimeOffset.MaxValue)]);
        return new AgentTaskRunPreview(
            agent.Id,
            agent.Name,
            resolvedModel.ProviderName,
            resolvedModel.ModelName,
            complexity,
            request.Options.MaxOutputTokens,
            resolvedModel.ReasoningEffort,
            CountPromptCharacters(request),
            UsesComplexModel(agent, resolvedModel));
    }

    private static AgentDefinition ResolveAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        return agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId)
            ?? agents.FirstOrDefault(candidate => candidate.Status == AgentStatus.Available && candidate.Role == task.RequiredRole)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
    }

    private static string BuildStartMessage(AgentDefinition agent, ModelProfile resolvedModel)
    {
        return $"{agent.Name} started task (model: {resolvedModel.ModelName}).";
    }

    private static ModelRequest BuildRequest(
        Goal goal,
        TaskSpec task,
        AgentDefinition agent,
        ModelProfile resolvedModel,
        TaskComplexity complexity,
        IReadOnlyList<ProgressEvent>? pendingTimelineEvents = null)
    {
        var isLocal = LocalModelPromptOptimizer.IsLocalProvider(resolvedModel);

        var baseSystemPrompt = BuildSystemPrompt(agent.Role, complexity);

        var systemPrompt = isLocal
            ? LocalModelPromptOptimizer.OptimizeSystemPrompt(baseSystemPrompt, agent.Role)
            : baseSystemPrompt;

        var timeline = string.Join(
            Environment.NewLine,
            PromptContextFormatter.SelectPromptTimelineEvents(
                    goal.Timeline
                        .Concat(pendingTimelineEvents ?? [])
                        .Where(evt => evt.TaskId == task.Id || evt.TaskId is null),
                    maxEvents: TimelineEventBudget(complexity),
                    complexity)
                .Select(evt => PromptContextFormatter.FormatTimelineEvent(evt, includeTimestamp: false, complexity)));
        var responseBudgetGuidance = PromptContextFormatter.BuildResponseBudgetGuidance(complexity);
        var responseGuidance = string.IsNullOrWhiteSpace(responseBudgetGuidance)
            ? string.Empty
            : $"Response guidance: {responseBudgetGuidance}{Environment.NewLine}";
        var modelFitGuidance = BuildModelFitGuidance(resolvedModel);
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
            modelFitGuidance +
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

        if (LocalModelPromptOptimizer.IsLocalProvider(resolvedModel))
        {
            return complexity == TaskComplexity.Complex
                ? ComplexLocalProviderFallbackMaxOutputTokens
                : RoutineLocalProviderFallbackMaxOutputTokens;
        }

        return complexity == TaskComplexity.Complex
            ? ComplexPaidProviderFallbackMaxOutputTokens
            : RoutinePaidProviderFallbackMaxOutputTokens;
    }

    private static List<ModelFitSummary> BuildModelFitSummary(Goal goal)
    {
        return ModelFitEvidence.BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes));
    }

    private static bool UsesComplexModel(AgentDefinition agent, ModelProfile resolvedModel)
    {
        return agent.ComplexModel is not null &&
            SameModel(agent.ComplexModel, resolvedModel);
    }

    private static bool SameModel(ModelProfile left, ModelProfile right)
    {
        return left.ProviderName.Equals(right.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            left.ModelName.Equals(right.ModelName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildModelFitGuidance(ModelProfile resolvedModel)
    {
        return $"Model fit reporting: Include a final line exactly like `{ModelFitEvidence.BuildNoteTemplate($"{resolvedModel.ProviderName}/{resolvedModel.ModelName}")}`.{Environment.NewLine}";
    }

    private static string BuildSystemPrompt(AgentRole role, TaskComplexity complexity)
    {
        if (complexity == TaskComplexity.Simple)
        {
            return $"You are the {role} agent. Complete the assigned task. " +
                "Report only changed files, verification evidence, blockers, or HUMAN_INPUT: <question>; " +
                "ground claims in files or command output.";
        }

        return $"You are the {role} agent in a software-development orchestrator. " +
            "Complete the assigned SDLC task, state concrete results, verification evidence, blockers, and any human input needed. " +
            "If you cannot proceed without operator input, include a line that starts with HUMAN_INPUT: followed by the exact question. " +
            "Avoid generic status summaries; ground conclusions in files, command output, or cited source material.";
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

    private static int CountPromptCharacters(ModelRequest request)
    {
        return request.SystemPrompt.Length + request.Messages.Sum(message => message.Content.Length);
    }

    private static string TrimForTimeline(string value)
    {
        const int maxLength = 240;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static bool HasOutputTokenLimitHit(TaskExecutionRecord execution)
    {
        return OutputTokenLimit.IsHit(execution);
    }
}
