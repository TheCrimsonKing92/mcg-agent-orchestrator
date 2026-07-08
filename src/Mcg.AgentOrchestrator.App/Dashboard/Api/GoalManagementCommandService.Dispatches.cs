using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
public static IReadOnlyList<WorkerProfileDispatchResult> ProfileDispatchReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    WorkerProfile profile,
    IReadOnlyList<AgentDefinition>? agents = null,
    IModelProviderRegistry? providers = null)
{
    GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        goal);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
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
    IReadOnlyList<AgentDefinition>? agents = null,
    IModelProviderRegistry? providers = null,
    bool allowPendingRecordedDispatchRefresh = false)
{
    GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        goal);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
    var subscriptionMetadata = TryBuildProfileSubscriptionMetadata(goal, task, profile, agents);
    return WorkerProfileDispatcher.PrepareTask(
        kernel,
        goal,
        task,
        profile,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        variables: subscriptionMetadata?.Variables,
        providerName: subscriptionMetadata?.ProviderName,
        modelName: subscriptionMetadata?.ModelName,
        reasoningEffort: subscriptionMetadata?.ReasoningEffort,
        taskComplexity: subscriptionMetadata?.Complexity,
        usesComplexModel: false,
        preflightFindings: null,
        allowPendingRecordedDispatchRefresh: allowPendingRecordedDispatchRefresh);
}

public static WorkerProfileDispatchResult RefreshPreparedDispatchBeforeStart(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null)
{
    var lastDispatch = task.LastDispatch
        ?? throw new InvalidOperationException($"Task '{task.Id}' has no dispatch to refresh before start.");
    if (task.LastProcess is not null)
    {
        throw new InvalidOperationException($"Task '{task.Id}' already has a dispatch process record; refresh, cancel, or retry before starting it again.");
    }

    var resolvedAgents = agents ?? AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
    var resolvedProfiles = profiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var profile = resolvedProfiles.Profiles.FirstOrDefault(candidate =>
        candidate.Name.Equals(lastDispatch.WorkerName, StringComparison.OrdinalIgnoreCase));
    if (profile is null)
    {
        throw new InvalidOperationException(
            $"Cannot refresh dispatch for task '{task.Id}' before start because worker profile '{lastDispatch.WorkerName}' is not available.");
    }

    return ProfileDispatchTask(
        kernel,
        workspace,
        goal,
        task,
        profile,
        resolvedAgents,
        providers,
        allowPendingRecordedDispatchRefresh: true);
}

public static IReadOnlyList<WorkerProfileDispatchResult> RefreshPreparedDispatchesBeforeStart(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null)
{
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
    var resolvedAgents = agents ?? AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
    var resolvedProfiles = profiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var refreshed = new List<WorkerProfileDispatchResult>();

    foreach (var item in plan.Items.Where(item => item.Status == ProcessBatchItemStatus.Ready))
    {
        var task = goal.Tasks.Single(task => task.Id == item.TaskId);
        var dispatch = RefreshPreparedDispatchBeforeStart(
            kernel,
            workspace,
            goal,
            task,
            resolvedAgents,
            resolvedProfiles,
            providers);
        refreshed.Add(dispatch);
    }

    return refreshed;
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
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null)
{
    return SubscriptionDispatchReadyBatch(kernel, workspace, goal, agents, profiles, providers).Dispatches;
}

public static WorkerProfileReadyBatchResult SubscriptionDispatchReadyBatch(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null)
{
    GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        goal);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents);
    return WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
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
    bool allowGitReference = false,
    IModelProviderRegistry? providers = null)
{
    GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        goal);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
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
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null,
    bool approveHighRiskOwnership = false,
    Action<AgentOrchestratorKernel, GoalId, TaskId>? checkpointBeforeWorkerStart = null)
{
    GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        goal);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, goal);
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents, approveHighRiskOwnership);
    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        safeBatch.TaskIds);
    if (checkpointBeforeWorkerStart is not null && batch.Dispatches.Count > 0)
    {
        checkpointBeforeWorkerStart(kernel, goal.Id, batch.Dispatches[0].Task.Id);
    }

    var processes = StartDispatches(
        kernel,
        workspace,
        goal,
        batch.Dispatches.Select(dispatch => dispatch.Task.Id).ToHashSet(),
        refreshBeforeStart: false,
        agents,
        profiles,
        providers,
        checkpointBeforeWorkerStart);
    return new SubscriptionStartResult(batch.Dispatches, processes, safeBatch.Plan, batch.Blocked);
}

private static ParallelSafeBatchSelection SelectFirstParallelSafeAssignedBatch(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    bool approveHighRiskOwnership = false)
{
    var assigned = goal.Tasks
        .Where(IsSubscriptionStartCandidate)
        .ToList();
    var plan = BuildReadyTaskParallelPlan(goal, agents, approveHighRiskOwnership);
    var firstBatch = plan.Batches.FirstOrDefault();
    var taskIds = firstBatch is null
        ? []
        : assigned
            .Where(task => firstBatch.IntentIds.Contains(task.Id.Value, StringComparer.OrdinalIgnoreCase))
            .Select(task => task.Id)
            .ToHashSet();
    return new ParallelSafeBatchSelection(taskIds, plan);
}

public static ParallelExecutionPlan BuildReadyTaskParallelPlan(
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    bool approveHighRiskOwnership = false)
{
    var assigned = goal.Tasks
        .Where(IsSubscriptionStartCandidate)
        .ToList();
    var intents = assigned
        .Select(task => new ParallelExecutionIntent(
            task.Id.Value,
            goal.Id.Value,
            InferParallelFileScopes(goal, task),
            ProviderKey: ResolveParallelProviderKey(task, agents),
            DependsOn: BuildIncompleteEarlierStageDependencies(goal, task)))
        .ToList();
    var providerQuotas = intents
        .Where(intent => !string.IsNullOrWhiteSpace(intent.ProviderKey))
        .Select(intent => intent.ProviderKey!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(provider => new ParallelExecutionProviderQuota(provider, 1))
        .ToList();
    return ParallelExecutionPlanner.Build(intents, providerQuotas, approveHighRiskOwnership);
}

private sealed record ParallelSafeBatchSelection(HashSet<TaskId> TaskIds, ParallelExecutionPlan Plan);

private static bool IsSubscriptionStartCandidate(TaskSpec task)
{
    return task.Status == WorkTaskStatus.Assigned &&
        task.LastProcess is not { IsRunning: true };
}

internal static bool HasAssignedDispatchCandidates(Goal goal) =>
    goal.Tasks.Any(IsSubscriptionStartCandidate);

private static string[] BuildIncompleteEarlierStageDependencies(Goal goal, TaskSpec task)
{
    return goal.Tasks
        .Where(candidate => IsEarlierSdlcStage(candidate.RequiredRole, task.RequiredRole))
        .Where(candidate => candidate.Status != WorkTaskStatus.Completed)
        .Select(candidate => candidate.Id.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

private static bool IsEarlierSdlcStage(AgentRole candidate, AgentRole current)
{
    return SdlcStageOrder(candidate) is { } candidateOrder &&
        SdlcStageOrder(current) is { } currentOrder &&
        candidateOrder < currentOrder;
}

internal static bool IsEarlierSdlcStageOf(AgentRole candidate, AgentRole current) =>
    IsEarlierSdlcStage(candidate, current);

private static int? SdlcStageOrder(AgentRole role)
{
    return role switch
    {
        AgentRole.Planner => 0,
        AgentRole.Researcher => 1,
        AgentRole.Developer => 2,
        AgentRole.Tester => 3,
        AgentRole.Reviewer => 4,
        _ => null
    };
}

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

public static ProcessBatchExecutionResult StartDispatches(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null,
    bool refreshBeforeStart = true,
    Action<AgentOrchestratorKernel, GoalId, TaskId>? checkpointBeforeWorkerStart = null)
{
    return StartDispatches(
        kernel,
        workspace,
        goal,
        taskIdsToStart: null,
        refreshBeforeStart,
        agents,
        profiles,
        providers,
        checkpointBeforeWorkerStart);
}

private static ProcessBatchExecutionResult StartDispatches(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    HashSet<TaskId>? taskIdsToStart,
    bool refreshBeforeStart,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null,
    Action<AgentOrchestratorKernel, GoalId, TaskId>? checkpointBeforeWorkerStart = null)
{
    var runner = new BackgroundDispatchRunner();
    var logRoot = workspace.LogDirectory;
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
    var started = new List<TaskSpec>();
    var recoveryActions = new List<WorkerSandboxPrepRecoverableAction>();
    IReadOnlyList<AgentDefinition>? resolvedAgents = null;
    WorkerProfileCatalog? resolvedProfiles = null;

    foreach (var item in plan.Items.Where(item =>
        item.Status == ProcessBatchItemStatus.Ready &&
        (taskIdsToStart is null || taskIdsToStart.Contains(item.TaskId))))
    {
        var task = goal.Tasks.Single(task => task.Id == item.TaskId);
        if (refreshBeforeStart)
        {
            resolvedAgents ??= agents ?? AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
            resolvedProfiles ??= profiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath);
            RefreshPreparedDispatchBeforeStart(
                kernel,
                workspace,
                goal,
                task,
                resolvedAgents,
                resolvedProfiles,
                providers);
        }

        var startResult = runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot, checkpointBeforeWorkerStart);
        if (startResult.RecoveryAction is { } action)
        {
            recoveryActions.Add(action);
            continue;
        }

        started.Add(task);
    }

    return new ProcessBatchExecutionResult(plan, started, recoveryActions);
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
