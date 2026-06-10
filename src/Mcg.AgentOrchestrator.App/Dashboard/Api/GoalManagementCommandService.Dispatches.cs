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
        workspace.ExecutionDirectory,
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
    return WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ExecutionDirectory,
        DateTimeOffset.UtcNow);
}

public static WorkerProfileDispatchResult SubscriptionDispatchTask(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles)
{
    return WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ExecutionDirectory,
        DateTimeOffset.UtcNow);
}

public static SubscriptionStartResult StartSubscriptionReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles)
{
    var dispatches = SubscriptionDispatchReadyTasks(kernel, workspace, goal, agents, profiles);
    var processes = StartDispatches(
        kernel,
        workspace,
        goal,
        dispatches.Select(dispatch => dispatch.Task.Id).ToHashSet());
    return new SubscriptionStartResult(dispatches, processes);
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
