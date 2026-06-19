using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
public static IReadOnlyList<WorkerProfileDispatchResult> ProfileDispatchReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    WorkerProfile profile,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var results = new List<WorkerProfileDispatchResult>();
    foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned).ToList())
    {
        results.Add(ProfileDispatchTask(kernel, workspace, goal, task, profile, agents));
    }

    return results;
}

public static WorkerProfileDispatchResult ProfileDispatchTask(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    WorkerProfile profile,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var subscriptionMetadata = TryBuildProfileSubscriptionMetadata(goal, task, profile, agents);
    return WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        subscriptionMetadata?.Variables,
        subscriptionMetadata?.ProviderName,
        subscriptionMetadata?.ModelName,
        subscriptionMetadata?.ReasoningEffort,
        subscriptionMetadata?.Complexity);
}

private static ProfileSubscriptionMetadata? TryBuildProfileSubscriptionMetadata(
    Goal goal,
    TaskSpec task,
    WorkerProfile profile,
    IReadOnlyList<AgentDefinition>? agents)
{
    if (agents is null)
    {
        return null;
    }

    AgentDefinition agent;
    try
    {
        agent = ResolveAssignedAgent(task, agents);
    }
    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
    {
        return null;
    }

    if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
    {
        return null;
    }

    string profileName;
    try
    {
        profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task);
    }
    catch (InvalidOperationException)
    {
        return null;
    }

    if (!profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var variables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(agent, goal, task);
    var providerName = variables.GetValueOrDefault("providerName");
    var modelName = variables.GetValueOrDefault("subscriptionModelName");
    var reasoningEffort = variables.GetValueOrDefault("subscriptionReasoningEffort");
    var complexity = Enum.TryParse<TaskComplexity>(variables.GetValueOrDefault("taskComplexity"), out var parsedComplexity)
        ? parsedComplexity
        : (TaskComplexity?)null;
    return new ProfileSubscriptionMetadata(variables, providerName, modelName, reasoningEffort, complexity);
}

private sealed record ProfileSubscriptionMetadata(
    IReadOnlyDictionary<string, string?> Variables,
    string? ProviderName,
    string? ModelName,
    string? ReasoningEffort,
    TaskComplexity? Complexity);

public static IReadOnlyList<WorkerProfileDispatchResult> SubscriptionDispatchReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles)
{
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents);
    return WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        safeBatch.TaskIds);
}

public static WorkerProfileDispatchResult SubscriptionDispatchTask(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    DispatchModelOverride? modelOverride = null,
    bool allowGitReference = false)
{
    return WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        modelOverride,
        allowGitReference);
}

public static SubscriptionStartResult StartSubscriptionReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles)
{
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents);
    var dispatches = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        safeBatch.TaskIds);
    var processes = StartDispatches(
        kernel,
        workspace,
        goal,
        dispatches.Select(dispatch => dispatch.Task.Id).ToHashSet());
    return new SubscriptionStartResult(dispatches, processes, safeBatch.Plan);
}

private static ParallelSafeBatchSelection SelectFirstParallelSafeAssignedBatch(Goal goal, IReadOnlyList<AgentDefinition> agents)
{
    var assigned = goal.Tasks
        .Where(task => task.Status == WorkTaskStatus.Assigned)
        .ToList();
    var plan = BuildReadyTaskParallelPlan(goal, agents);
    var firstBatch = plan.Batches.FirstOrDefault();
    var taskIds = firstBatch is null
        ? []
        : assigned
            .Where(task => firstBatch.IntentIds.Contains(task.Id.Value, StringComparer.OrdinalIgnoreCase))
            .Select(task => task.Id)
            .ToHashSet();
    return new ParallelSafeBatchSelection(taskIds, plan);
}

public static ParallelExecutionPlan BuildReadyTaskParallelPlan(Goal goal, IReadOnlyList<AgentDefinition>? agents = null)
{
    var assigned = goal.Tasks
        .Where(task => task.Status == WorkTaskStatus.Assigned)
        .ToList();
    var intents = assigned
        .Select(task => new ParallelExecutionIntent(
            task.Id.Value,
            goal.Id.Value,
            InferParallelFileScopes(goal, task),
            ProviderKey: ResolveParallelProviderKey(task, agents)))
        .ToList();
    var providerQuotas = intents
        .Where(intent => !string.IsNullOrWhiteSpace(intent.ProviderKey))
        .Select(intent => intent.ProviderKey!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(provider => new ParallelExecutionProviderQuota(provider, 1))
        .ToList();
    return ParallelExecutionPlanner.Build(intents, providerQuotas);
}

private sealed record ParallelSafeBatchSelection(HashSet<TaskId> TaskIds, ParallelExecutionPlan Plan);

private static string[] InferParallelFileScopes(Goal goal, TaskSpec task)
{
    var text = $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}";
    var matches = System.Text.RegularExpressions.Regex
        .Matches(text, @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents)[\\/][A-Za-z0-9_.\\/\-]+")
        .Select(match => match.Value.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']'))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return matches;
}

private static string? ResolveParallelProviderKey(TaskSpec task, IReadOnlyList<AgentDefinition>? agents)
{
    if (task.AssignedAgentId is null || agents is null)
    {
        return null;
    }

    var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
    return agent?.Model.ProviderName;
}

public static ProcessBatchExecutionResult StartDispatches(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, Goal goal)
{
    return StartDispatches(kernel, workspace, goal, taskIdsToStart: null);
}

private static ProcessBatchExecutionResult StartDispatches(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    HashSet<TaskId>? taskIdsToStart)
{
    var runner = new BackgroundDispatchRunner();
    var logRoot = workspace.LogDirectory;
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
    var started = new List<TaskSpec>();

    foreach (var item in plan.Items.Where(item =>
        item.Status == ProcessBatchItemStatus.Ready &&
        (taskIdsToStart is null || taskIdsToStart.Contains(item.TaskId))))
    {
        var task = goal.Tasks.Single(task => task.Id == item.TaskId);
        runner.StartLatestDispatch(kernel, goal.Id, task.Id, logRoot);
        started.Add(task);
    }

    return new ProcessBatchExecutionResult(plan, started);
}

public static ProcessBatchExecutionResult RefreshDispatches(AgentOrchestratorKernel kernel, Goal goal)
{
    var runner = new BackgroundDispatchRunner();
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.RefreshDispatches);
    var refreshed = new List<TaskSpec>();

    foreach (var item in plan.Items.Where(item => item.Status == ProcessBatchItemStatus.Ready))
    {
        var task = goal.Tasks.Single(task => task.Id == item.TaskId);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);
        refreshed.Add(task);
    }

    return new ProcessBatchExecutionResult(plan, refreshed);
}

public static ProcessBatchExecutionResult CancelDispatches(AgentOrchestratorKernel kernel, Goal goal)
{
    var runner = new BackgroundDispatchRunner();
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.CancelDispatches);
    var cancelled = new List<TaskSpec>();

    foreach (var item in plan.Items.Where(item => item.Status == ProcessBatchItemStatus.Ready))
    {
        var task = goal.Tasks.Single(task => task.Id == item.TaskId);
        runner.CancelLatestProcess(kernel, goal.Id, task.Id);
        cancelled.Add(task);
    }

    return new ProcessBatchExecutionResult(plan, cancelled);
}
}
