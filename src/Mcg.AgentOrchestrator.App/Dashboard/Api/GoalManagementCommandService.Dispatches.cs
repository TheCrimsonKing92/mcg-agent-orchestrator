using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
internal static Func<int, bool> IsTrackedProcessRunningForReadyBatch { get; set; } = IsProcessRunning;

public static IReadOnlyList<WorkerProfileDispatchResult> ProfileDispatchReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    WorkerProfile profile,
    IReadOnlyList<AgentDefinition>? agents = null,
    IModelProviderRegistry? providers = null)
{
    var results = new List<WorkerProfileDispatchResult>();
    var assigned = goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned).ToList();
    if (assigned.Count > 0 &&
        GoalRefinementWorkCoordinator.HasPendingWork(goal) &&
        assigned.All(task => task.RequiredRole != AgentRole.Researcher))
    {
        EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
    }

    foreach (var task in assigned.Where(task => IsRefinementEligible(goal, task)))
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
    bool allowPendingRecordedDispatchRefresh = false,
    int? reviewAutoRetryStopRound = null,
    WorkerSandboxOptions? sandboxOptions = null)
{
    EnsureRefinedForTask(kernel, workspace, providers, goal, task);
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
        reasoningEffortReason: subscriptionMetadata?.ReasoningEffortReason,
        preflightFindings: null,
        allowPendingRecordedDispatchRefresh: allowPendingRecordedDispatchRefresh,
        reviewRetryCap: task.RequiredRole == AgentRole.Reviewer
            ? ReviewRetryCapReceipt.Create(
                goal,
                ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound))
            : null,
        citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
        sandboxOptions: sandboxOptions);
}

public static WorkerProfileDispatchResult RefreshPreparedDispatchBeforeStart(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null,
    int? reviewAutoRetryStopRound = null,
    WorkerSandboxOptions? sandboxOptions = null)
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
        allowPendingRecordedDispatchRefresh: true,
        reviewAutoRetryStopRound: reviewAutoRetryStopRound,
        sandboxOptions: sandboxOptions);
}

public static IReadOnlyList<WorkerProfileDispatchResult> RefreshPreparedDispatchesBeforeStart(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    WorkerProfileCatalog? profiles = null,
    IModelProviderRegistry? providers = null,
    int? reviewAutoRetryStopRound = null,
    WorkerSandboxOptions? sandboxOptions = null)
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
            providers,
            reviewAutoRetryStopRound,
            sandboxOptions);
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

    var profiles = new WorkerProfileCatalog([profile]);
    string profileName;
    try
    {
        profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task, profiles);
    }
    catch (InvalidOperationException)
    {
        return null;
    }

    if (!profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var variables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(agent, goal, task, profiles);
    var providerName = variables.GetValueOrDefault("providerName");
    var modelName = variables.GetValueOrDefault("subscriptionModelName");
    var reasoningEffort = variables.GetValueOrDefault("subscriptionReasoningEffort");
    var reasoningEffortReason = variables.GetValueOrDefault("reasoningEffortSelectionReason");
    var complexity = Enum.TryParse<TaskComplexity>(variables.GetValueOrDefault("taskComplexity"), out var parsedComplexity)
        ? parsedComplexity
        : (TaskComplexity?)null;
    return new ProfileSubscriptionMetadata(variables, providerName, modelName, reasoningEffort, reasoningEffortReason, complexity);
}

private sealed record ProfileSubscriptionMetadata(
    IReadOnlyDictionary<string, string?> Variables,
    string? ProviderName,
    string? ModelName,
    string? ReasoningEffort,
    string? ReasoningEffortReason,
    TaskComplexity? Complexity);

public static IReadOnlyList<WorkerProfileDispatchResult> SubscriptionDispatchReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null,
    int? reviewAutoRetryStopRound = null,
    WorkerSandboxOptions? sandboxOptions = null)
{
    return SubscriptionDispatchReadyBatch(
        kernel,
        workspace,
        goal,
        agents,
        profiles,
        providers,
        reviewAutoRetryStopRound,
        sandboxOptions).Dispatches;
}

public static WorkerProfileReadyBatchResult SubscriptionDispatchReadyBatch(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null,
    int? reviewAutoRetryStopRound = null,
    WorkerSandboxOptions? sandboxOptions = null)
{
    ReconcileExitedAssignedProcessRecords(kernel, goal);
    goal = kernel.GetGoal(goal.Id);
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents);
    EnsureRefinedForSelectedTasks(kernel, workspace, providers, goal, safeBatch.TaskIds);
    goal = kernel.GetGoal(goal.Id);
    return WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        safeBatch.TaskIds,
        reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound),
        citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
        sandboxOptions: sandboxOptions);
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
    IModelProviderRegistry? providers = null,
    int? reviewAutoRetryStopRound = null)
{
    EnsureRefinedForTask(kernel, workspace, providers, goal, task);
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
        allowGitReference,
        reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound),
        citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace));
}

public static SubscriptionStartResult StartSubscriptionReadyTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    IModelProviderRegistry? providers = null,
    bool approveHighRiskOwnership = false,
    Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? checkpointBeforeWorkerStart = null,
    Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentInterruptedDispatchState = null,
    int? reviewAutoRetryStopRound = null,
    BackgroundDispatchRunner? runner = null,
    WorkerSandboxOptions? sandboxOptions = null)
{
    ReconcileExitedAssignedProcessRecords(kernel, goal);
    goal = kernel.GetGoal(goal.Id);
    var safeBatch = SelectFirstParallelSafeAssignedBatch(goal, agents, approveHighRiskOwnership);
    EnsureRefinedForSelectedTasks(kernel, workspace, providers, goal, safeBatch.TaskIds);
    goal = kernel.GetGoal(goal.Id);
    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        profiles,
        workspace.PromptDirectory,
        workspace.ResolveExecutionDirectory(goal.Id),
        DateTimeOffset.UtcNow,
        safeBatch.TaskIds,
        reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound),
        citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
        sandboxOptions: sandboxOptions);
    var containsInterruptedDispatchRecovery = batch.Dispatches.Any(dispatch =>
        dispatch.Task.InterruptedDispatchRecoveryId is not null);
    if (!containsInterruptedDispatchRecovery &&
        checkpointBeforeWorkerStart is not null &&
        batch.Dispatches.Count > 0)
    {
        checkpointBeforeWorkerStart(
            kernel,
            goal.Id,
            batch.Dispatches[0].Task.Id,
            DispatchRecordCheckpointPhase.BeforeProcessStart);
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
        checkpointBeforeWorkerStart,
        readCurrentInterruptedDispatchState,
        runner: runner,
        sandboxOptions: sandboxOptions);
    return new SubscriptionStartResult(
        batch.Dispatches,
        processes,
        safeBatch.Plan,
        safeBatch.Blocked.Concat(batch.Blocked).ToList());
}

private static CitedPriorEvidenceResolver CreateCitedPriorEvidenceResolver(OrchestratorWorkspace workspace) =>
    CitedPriorEvidenceResolver.ForStateDatabase(workspace.SqliteStatePath);

private static int ResolveReviewAutoRetryStopRound(
    OrchestratorWorkspace workspace,
    int? reviewAutoRetryStopRound) =>
    reviewAutoRetryStopRound ??
    ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
        new DirectoryInfo(workspace.OrchestratorDirectory)).ReviewAutoRetryStopRound;

private static void EnsureRefinedForSelectedTasks(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry? providers,
    Goal goal,
    IReadOnlySet<TaskId> taskIds)
{
    var selected = goal.Tasks.Where(task => taskIds.Contains(task.Id)).ToArray();
    if (selected.Length == 0)
    {
        var assignedCandidates = goal.Tasks.Where(IsSubscriptionStartCandidate).ToArray();
        if (GoalRefinementWorkCoordinator.HasPendingWork(goal) &&
            assignedCandidates.Length > 0 &&
            assignedCandidates.All(task => task.RequiredRole != AgentRole.Researcher))
        {
            EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
        }

        return;
    }

    if (goal.RefinedSpec is null && selected.All(task => task.RequiredRole == AgentRole.Researcher))
        return;

    EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
}

private static void EnsureRefinedForTask(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry? providers,
    Goal goal,
    TaskSpec task)
{
    if (goal.RefinedSpec is null && task.RequiredRole == AgentRole.Researcher)
        return;

    EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
}

internal static void EnsureRefinedForSpecConsumer(
    AgentOrchestratorKernel kernel,
    OrchestratorWorkspace workspace,
    IModelProviderRegistry? providers,
    Goal goal)
{
    var current = kernel.GetGoal(goal.Id);
    if (GoalRefinementWorkCoordinator.HasPendingWork(current))
    {
        var launch = GoalRefinementWorkCoordinator.TryLaunch(workspace, goal.Id);
        throw new InvalidOperationException(
            $"SPEC_REFINEMENT_PENDING goal={goal.Id.Value} owner=durable-outbox " +
            $"executor_started={launch.Started.ToString().ToLowerInvariant()} detail={launch.Detail}");
    }

    _ = GoalRefinementGate.EnsureRefined(
        kernel,
        workspace,
        providers ?? new InMemoryModelProviderRegistry([]),
        current);
    GoalRefinementGate.ThrowIfAwaitingClarification(workspace, kernel.GetGoal(goal.Id));
}

private static ParallelSafeBatchSelection SelectFirstParallelSafeAssignedBatch(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    bool approveHighRiskOwnership = false)
{
    var assigned = goal.Tasks
        .Where(IsSubscriptionStartCandidate)
        .Where(task => IsRefinementEligible(goal, task))
        .ToList();
    var plan = BuildReadyTaskParallelPlan(goal, agents, approveHighRiskOwnership);
    var firstBatch = plan.Batches.FirstOrDefault();
    var taskIds = firstBatch is null
        ? []
        : assigned
            .Where(task => firstBatch.IntentIds.Contains(task.Id.Value, StringComparer.OrdinalIgnoreCase))
            .Select(task => task.Id)
            .ToHashSet();
    return new ParallelSafeBatchSelection(
        taskIds,
        plan,
        BuildAssignedTaskExclusionDiagnostics(goal, agents, plan, taskIds));
}

public static ParallelExecutionPlan BuildReadyTaskParallelPlan(
    Goal goal,
    IReadOnlyList<AgentDefinition>? agents = null,
    bool approveHighRiskOwnership = false)
{
    var assigned = goal.Tasks
        .Where(IsSubscriptionStartCandidate)
        .Where(task => IsRefinementEligible(goal, task))
        .ToList();
    var intents = assigned
        .Select(task =>
        {
            var scope = GoalFileScopeInference.ForScheduling(goal, task);
            return new ParallelExecutionIntent(
                task.Id.Value,
                goal.Id.Value,
                scope.Includes,
                ProviderKey: ResolveParallelProviderKey(task, agents),
                DependsOn: BuildIncompleteEarlierStageDependencies(goal, task),
                ScopeConfidence: scope.Confidence);
        })
        .ToList();
    var providerQuotas = intents
        .Where(intent => !string.IsNullOrWhiteSpace(intent.ProviderKey))
        .Select(intent => intent.ProviderKey!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(provider => new ParallelExecutionProviderQuota(provider, 1))
        .ToList();
    return ParallelExecutionPlanner.Build(intents, providerQuotas, approveHighRiskOwnership);
}

private static bool IsRefinementEligible(Goal goal, TaskSpec task) =>
    !GoalRefinementWorkCoordinator.HasPendingWork(goal) ||
    task.RequiredRole == AgentRole.Researcher;

private sealed record ParallelSafeBatchSelection(
    HashSet<TaskId> TaskIds,
    ParallelExecutionPlan Plan,
    IReadOnlyList<ReadyBlockedDiagnostic> Blocked);

    private static bool IsSubscriptionStartCandidate(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Assigned &&
            !HasBlockingRunningProcess(task) &&
            !WorkerProfileDispatcher.IsTaskRetryDeferred(task, DateTimeOffset.UtcNow, out _);
    }

internal static bool HasAssignedDispatchCandidates(Goal goal) =>
    goal.Tasks.Any(IsSubscriptionStartCandidate);

private static bool HasBlockingRunningProcess(TaskSpec task)
{
    return task.LastProcess is { IsRunning: true };
}

private static IReadOnlyList<ReadyBlockedDiagnostic> BuildAssignedTaskExclusionDiagnostics(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    ParallelExecutionPlan plan,
    IReadOnlySet<TaskId> selectedTaskIds)
{
    var diagnostics = new List<ReadyBlockedDiagnostic>();
    var decisionsByTaskId = plan.Decisions.ToDictionary(decision => decision.IntentId, StringComparer.OrdinalIgnoreCase);

    foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
    {
        if (selectedTaskIds.Contains(task.Id))
        {
            continue;
        }

        if (task.LastProcess is { IsRunning: true } process)
        {
            diagnostics.Add(BuildReadyBlockedDiagnostic(
                goal,
                task,
                agents,
                "last-process-running",
                [$"LastProcess.IsRunning is true for pid {process.ProcessId}; exit artifact path {process.ExitCodePath}"]));
            continue;
        }

        if (decisionsByTaskId.TryGetValue(task.Id.Value, out var decision))
        {
            if (decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval &&
                decision.Reasons.Any(reason => reason.Contains("operator approval", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            diagnostics.Add(BuildReadyBlockedDiagnostic(
                goal,
                task,
                agents,
                decision.Disposition == ParallelExecutionDisposition.RequiresOperatorApproval
                    ? "unmet-dependency"
                    : "parallel-serialized",
                decision.Reasons));
            continue;
        }

        var predecessor = goal.Tasks.FirstOrDefault(candidate =>
            IsEarlierSdlcStage(candidate.RequiredRole, task.RequiredRole) &&
            candidate.Status != WorkTaskStatus.Completed);
        if (predecessor is not null)
        {
            diagnostics.Add(BuildReadyBlockedDiagnostic(
                goal,
                task,
                agents,
                "unmet-dependency",
                [$"predecessor {predecessor.Id.Value} is {predecessor.Status}, not Completed"]));
        }
    }

    return diagnostics;
}

private static ReadyBlockedDiagnostic BuildReadyBlockedDiagnostic(
    Goal goal,
    TaskSpec task,
    IReadOnlyList<AgentDefinition> agents,
    string reason,
    IReadOnlyList<string>? details = null)
{
    return new ReadyBlockedDiagnostic(
        goal.Id.Value[..8],
        TaskDisplayNumber.Resolve(goal, task.Id),
        task.Id.Value,
        ResolveReadyBlockedProvider(task, agents),
        reason,
        details);
}

private static string ResolveReadyBlockedProvider(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
{
    if (task.AssignedAgentId is null)
    {
        return "unknown";
    }

    var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
    return agent?.Subscription?.WorkerProfileName ?? agent?.Model.ProviderName ?? "unknown";
}

private static void ReconcileExitedAssignedProcessRecords(AgentOrchestratorKernel kernel, Goal goal)
{
    foreach (var task in goal.Tasks)
    {
        if (task.Status != WorkTaskStatus.Assigned ||
            task.LastProcess is not { IsRunning: true } process ||
            !DispatchExitArtifacts.TryRead(process.ExitCodePath, out var exitArtifact) ||
            exitArtifact.Origin == DispatchExitArtifactOrigin.Synthetic ||
            HasLiveTrackedProcess(process))
        {
            continue;
        }

        var completed = process with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            ExitCode = exitArtifact.ExitCode,
            ExitArtifactOrigin = exitArtifact.Origin,
            ExitArtifactReason = exitArtifact.Reason
        };
        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, completed, verification: null);
        kernel.RecordTaskNote(
            goal.Id,
            task.Id,
            $"Auto-cleared stale LastProcess.IsRunning before dispatch; pid {process.ProcessId} had exit artifact {process.ExitCodePath} with exit {exitArtifact.ExitCode}.");
    }
}

private static bool HasLiveTrackedProcess(TaskProcessRecord process)
{
    var processIds = new HashSet<int>(process.TrackedProcessIds);
    var heartbeat = ProcessLogReader.ReadHeartbeat(process);
    if (heartbeat.IsAvailable)
    {
        if (heartbeat.ChildProcessId is { } childPid)
        {
            processIds.Add(childPid);
        }

        foreach (var ownedPid in heartbeat.OwnedProcessIds)
        {
            processIds.Add(ownedPid);
        }
    }

    return processIds.Any(IsTrackedProcessRunningForReadyBatch);
}

private static bool IsProcessRunning(int processId)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return true;
    }
}

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
        AgentRole.Researcher => 0,
        AgentRole.Planner => 1,
        AgentRole.Developer => 2,
        AgentRole.Tester => 3,
        AgentRole.Reviewer => 4,
        _ => null
    };
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
    Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? checkpointBeforeWorkerStart = null,
    Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentInterruptedDispatchState = null,
    int? reviewAutoRetryStopRound = null,
    BackgroundDispatchRunner? runner = null,
    WorkerSandboxOptions? sandboxOptions = null)
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
        checkpointBeforeWorkerStart,
        readCurrentInterruptedDispatchState,
        reviewAutoRetryStopRound,
        runner,
        sandboxOptions);
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
    Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? checkpointBeforeWorkerStart = null,
    Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentInterruptedDispatchState = null,
    int? reviewAutoRetryStopRound = null,
    BackgroundDispatchRunner? runner = null,
    WorkerSandboxOptions? sandboxOptions = null)
{
    runner ??= new BackgroundDispatchRunner();
    var logRoot = workspace.LogDirectory;
    var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
    var started = new List<TaskSpec>();
    var recoveryActions = new List<WorkerSandboxPrepRecoverableAction>();
    var startFailures = new List<DispatchProcessStartFailure>();
    var requeueSkippedCount = 0;
    // Once any dispatch host has been created, every later checkpoint in this goal batch is on the
    // orphan-sensitive side of the boundary, including the next task's nominal pre-start checkpoint.
    var processMayHaveStarted = false;
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
                providers,
                reviewAutoRetryStopRound,
                sandboxOptions);
        }

        Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? batchCheckpoint =
            checkpointBeforeWorkerStart is null
                ? null
                : (checkpointKernel, checkpointGoalId, checkpointTaskId, requestedPhase) =>
                {
                    checkpointBeforeWorkerStart(
                        checkpointKernel,
                        checkpointGoalId,
                        checkpointTaskId,
                        ResolveBatchCheckpointPhase(ref processMayHaveStarted, requestedPhase));
                };
        var startResult = runner.TryStartLatestDispatch(
            kernel,
            goal.Id,
            task.Id,
            logRoot,
            batchCheckpoint,
            readCurrentInterruptedDispatchState,
            sandboxOptions);
        if (startResult.RecoveryAction is { } action)
        {
            recoveryActions.Add(action);
            continue;
        }

        if (startResult.RequeueSkipped)
        {
            requeueSkippedCount++;
            continue;
        }

        if (startResult.FailureReason is { } failureReason)
        {
            startFailures.Add(new DispatchProcessStartFailure(task.Id, failureReason));
            continue;
        }

        started.Add(task);
    }

    return new ProcessBatchExecutionResult(plan, started, recoveryActions, requeueSkippedCount, startFailures);
}

internal static DispatchRecordCheckpointPhase ResolveBatchCheckpointPhase(
    ref bool processMayHaveStarted,
    DispatchRecordCheckpointPhase requestedPhase)
{
    processMayHaveStarted |= requestedPhase == DispatchRecordCheckpointPhase.ProcessMayHaveStarted;
    return processMayHaveStarted
        ? DispatchRecordCheckpointPhase.ProcessMayHaveStarted
        : DispatchRecordCheckpointPhase.BeforeProcessStart;
}

public static ProcessBatchExecutionResult RefreshDispatches(
    AgentOrchestratorKernel kernel,
    Goal goal,
    BackgroundDispatchRunner? runner = null)
{
    runner ??= new BackgroundDispatchRunner();
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
