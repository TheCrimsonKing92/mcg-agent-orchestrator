using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Owns goal dispatch preparation, refresh and cancellation. This is a UI-free application
/// operation: it takes Core/Infrastructure inputs, returns Core/Infrastructure and application
/// results, and never reaches into presentation to perform work.
/// </summary>
/// <remarks>
/// The process-liveness and process-identity probes are constructor-injected into readonly instance
/// fields. Two instances therefore cannot observe or mutate each other's probes, which is what lets
/// concurrent conductor dispatches and concurrent tests run without a shared override.
/// </remarks>
internal sealed partial class GoalDispatchOperations
{
    internal static InvalidOperationException SpecRefinementPendingException(
        string message, OrchestratorStateOutboxStatus status)
    {
        var exception = new InvalidOperationException(message);
        exception.Data[DispatchStartOutcome.HoldOwnerDataKey] = status is
            OrchestratorStateOutboxStatus.Pending or OrchestratorStateOutboxStatus.Processing
                ? ConductorHoldOwner.DurableOutbox : ConductorHoldOwner.None;
        return exception;
    }

    private readonly Func<int, bool> _isProcessRunning;
    private readonly Func<int, SpawnProcessIdentity?> _readProcessIdentity;

    public GoalDispatchOperations(
        Func<int, bool>? isProcessRunning = null,
        Func<int, SpawnProcessIdentity?>? readProcessIdentity = null)
    {
        _isProcessRunning = isProcessRunning ?? IsProcessRunning;
        _readProcessIdentity = readProcessIdentity ?? DispatchProcessIdentityEvidence.ReadCurrent;
    }

    public IReadOnlyList<WorkerProfileDispatchResult> ProfileDispatchReadyTasks(
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

        foreach (var task in assigned.Where(task => DispatchReadinessRules.IsRefinementEligible(goal, task)))
        {
            results.Add(ProfileDispatchTask(kernel, workspace, goal, task, profile, agents));
        }

        return results;
    }

    public WorkerProfileDispatchResult ProfileDispatchTask(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        IReadOnlyList<AgentDefinition>? agents = null,
        IModelProviderRegistry? providers = null,
        bool allowPendingRecordedDispatchRefresh = false,
        int? reviewAutoRetryStopRound = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null,
        WorkerProfileCatalog? subscriptionProfiles = null, bool? cascadeMechanicalReworkCheap = null)
    {
        EnsureRefinedForTask(kernel, workspace, providers, goal, task);
        var subscriptionMetadata = TryBuildProfileSubscriptionMetadata(goal, task, profile, agents,
            subscriptionProfiles, ResolveCascadeTesterCheapFirst(workspace, cascadeTesterCheapFirst, conductorPolicy),
            ResolveCascadeCheapModelAlias(workspace, cascadeCheapModelAlias, conductorPolicy), cascadeMechanicalReworkCheap: ResolveCascadeMechanicalReworkCheap(workspace, cascadeMechanicalReworkCheap, conductorPolicy));
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
            dispatchLane: subscriptionMetadata?.DispatchLane,
            modelSelectionReason: subscriptionMetadata?.ModelSelectionReason,
            preflightFindings: null,
            allowPendingRecordedDispatchRefresh: allowPendingRecordedDispatchRefresh,
            reviewRetryCap: task.RequiredRole == AgentRole.Reviewer
                ? ReviewRetryCapReceipt.Create(
                    goal,
                    ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound))
                : null,
            citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
            sandboxOptions: sandboxOptions,
            plannerSampleCount: ResolvePlannerSampleCount(workspace, plannerSampleCount),
            paidRoute: subscriptionMetadata?.PaidRoute ?? PaidRouteClassification.Unknown,
            shadowRecorder: DispatchShadowRecorder.Default);
    }

    public WorkerProfileDispatchResult RefreshPreparedDispatchBeforeStart(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition>? agents = null,
        WorkerProfileCatalog? profiles = null,
        IModelProviderRegistry? providers = null,
        int? reviewAutoRetryStopRound = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
    {
        var lastDispatch = task.LastDispatch
            ?? throw new InvalidOperationException($"Task '{task.Id}' has no dispatch to refresh before start.");
        if (task.LastProcess is not null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' already has a dispatch process record; refresh, cancel, or retry before starting it again.");
        }

        var resolvedAgents = agents ?? AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        var resolvedProfiles = profiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath);
        AgentDefinition assignedAgent;
        try
        {
            assignedAgent = AssignedAgentResolver.Resolve(task, resolvedAgents);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            throw AssignmentHold(
                DispatchAssignmentHoldCode.AgentNotFound,
                task,
                ex.Message,
                task.AssignedAgentId?.Value);
        }

        if (assignedAgent.Status != AgentStatus.Available)
        {
            throw AssignmentHold(
                DispatchAssignmentHoldCode.AgentUnavailable,
                task,
                $"Assigned agent '{assignedAgent.Id.Value}' is {assignedAgent.Status}; only Available agents may start a dispatch.",
                assignedAgent.Id.Value);
        }

        if (assignedAgent.Role != task.RequiredRole)
        {
            throw AssignmentHold(
                DispatchAssignmentHoldCode.RoleMismatch,
                task,
                $"Assigned agent '{assignedAgent.Id.Value}' has role {assignedAgent.Role}; task requires {task.RequiredRole}.",
                assignedAgent.Id.Value);
        }

        WorkerProfile profile;
        if (AgentExecutionPolicies.AllowsSubscription(assignedAgent.ExecutionPolicy))
        {
            string profileName;
            try
            {
                profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(
                    assignedAgent,
                    goal,
                    task,
                    resolvedProfiles,
                    sandboxOptions: sandboxOptions,
                    cascadeTesterCheapFirst: ResolveCascadeTesterCheapFirst(workspace, cascadeTesterCheapFirst, conductorPolicy),
                    cascadeCheapModelAlias: ResolveCascadeCheapModelAlias(workspace, cascadeCheapModelAlias, conductorPolicy), cascadeMechanicalReworkCheap: ResolveCascadeMechanicalReworkCheap(workspace, cascadeMechanicalReworkCheap, conductorPolicy));
                profile = resolvedProfiles.GetRequired(profileName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                throw AssignmentHold(
                    DispatchAssignmentHoldCode.ProfileUnavailable,
                    task,
                    $"Assigned agent '{assignedAgent.Id.Value}' has no available subscription profile: {ex.Message}",
                    assignedAgent.Id.Value,
                    assignedAgent.Subscription?.WorkerProfileName);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(lastDispatch.AssignedAgentId) ||
                !lastDispatch.AssignedAgentId.Equals(assignedAgent.Id.Value, StringComparison.OrdinalIgnoreCase))
            {
                var preparedAgent = string.IsNullOrWhiteSpace(lastDispatch.AssignedAgentId)
                    ? "an unrecorded legacy assignment"
                    : $"agent '{lastDispatch.AssignedAgentId}'";
                throw AssignmentHold(
                    DispatchAssignmentHoldCode.HarnessRebindUnsupported,
                    task,
                    $"Prepared harness '{lastDispatch.WorkerName}' belongs to {preparedAgent}, but the acknowledged assignment is API-only agent '{assignedAgent.Id.Value}'.",
                    assignedAgent.Id.Value,
                    lastDispatch.WorkerName);
            }

            try
            {
                profile = resolvedProfiles.GetRequired(lastDispatch.WorkerName);
            }
            catch (KeyNotFoundException ex)
            {
                throw AssignmentHold(
                    DispatchAssignmentHoldCode.ProfileUnavailable,
                    task,
                    ex.Message,
                    assignedAgent.Id.Value,
                    lastDispatch.WorkerName);
            }
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
            reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound, conductorPolicy),
            sandboxOptions: sandboxOptions,
            plannerSampleCount: ResolvePlannerSampleCount(workspace, plannerSampleCount, conductorPolicy),
            conductorPolicy: conductorPolicy, cascadeTesterCheapFirst: cascadeTesterCheapFirst,
            cascadeCheapModelAlias: cascadeCheapModelAlias, subscriptionProfiles: resolvedProfiles, cascadeMechanicalReworkCheap: cascadeMechanicalReworkCheap);
    }

    private static DispatchAssignmentHoldException AssignmentHold(
        DispatchAssignmentHoldCode code,
        TaskSpec task,
        string detail,
        string? assignedAgentId = null,
        string? workerProfileName = null)
    {
        var message = $"DISPATCH_ASSIGNMENT_HOLD code={code} task={task.Id.Value} detail={detail}";
        return new DispatchAssignmentHoldException(new DispatchAssignmentHold(
            code,
            message,
            assignedAgentId,
            workerProfileName));
    }

    public IReadOnlyList<WorkerProfileDispatchResult> RefreshPreparedDispatchesBeforeStart(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<AgentDefinition>? agents = null,
        WorkerProfileCatalog? profiles = null,
        IModelProviderRegistry? providers = null,
        int? reviewAutoRetryStopRound = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
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
                sandboxOptions,
                plannerSampleCount, conductorPolicy, cascadeTesterCheapFirst, cascadeCheapModelAlias, cascadeMechanicalReworkCheap);
            refreshed.Add(dispatch);
        }

        return refreshed;
    }

    private static ProfileSubscriptionMetadata? TryBuildProfileSubscriptionMetadata(
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        IReadOnlyList<AgentDefinition>? agents,
        WorkerProfileCatalog? subscriptionProfiles, bool cascadeTesterCheapFirst, string cascadeCheapModelAlias, bool cascadeMechanicalReworkCheap)
    {
        if (agents is null)
        {
            return null;
        }

        AgentDefinition agent;
        try
        {
            agent = AssignedAgentResolver.Resolve(task, agents);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }

        if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            return null;
        }

        var profiles = subscriptionProfiles ?? new WorkerProfileCatalog([profile]);
        string profileName;
        try
        {
            profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task, profiles,
                cascadeTesterCheapFirst: cascadeTesterCheapFirst, cascadeCheapModelAlias: cascadeCheapModelAlias, cascadeMechanicalReworkCheap: cascadeMechanicalReworkCheap);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var variables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(agent, goal, task, profiles,
            cascadeTesterCheapFirst: cascadeTesterCheapFirst, cascadeCheapModelAlias: cascadeCheapModelAlias, cascadeMechanicalReworkCheap: cascadeMechanicalReworkCheap);
        var providerName = variables.GetValueOrDefault("providerName");
        var modelName = variables.GetValueOrDefault("subscriptionModelName");
        var reasoningEffort = variables.GetValueOrDefault("subscriptionReasoningEffort");
        var reasoningEffortReason = variables.GetValueOrDefault("reasoningEffortSelectionReason");
        var complexity = Enum.TryParse<TaskComplexity>(variables.GetValueOrDefault("taskComplexity"), out var parsedComplexity)
            ? parsedComplexity
            : (TaskComplexity?)null;
        if (!Enum.TryParse<PaidRouteClassification>(variables.GetValueOrDefault("paidRoute"), out var paidRoute) ||
            paidRoute == PaidRouteClassification.Unknown)
        {
            throw new InvalidOperationException(
                $"Subscription profile '{profile.Name}' did not resolve an explicit paid-route classification.");
        }
        return new ProfileSubscriptionMetadata(
            variables,
            providerName,
            modelName,
            reasoningEffort,
            reasoningEffortReason,
            complexity,
            paidRoute, variables.GetValueOrDefault("dispatchLane"), variables.GetValueOrDefault("modelSelectionReason"));
    }

    private sealed record ProfileSubscriptionMetadata(
        IReadOnlyDictionary<string, string?> Variables,
        string? ProviderName,
        string? ModelName,
        string? ReasoningEffort,
        string? ReasoningEffortReason,
        TaskComplexity? Complexity,
        PaidRouteClassification PaidRoute, string? DispatchLane, string? ModelSelectionReason);

    public IReadOnlyList<WorkerProfileDispatchResult> SubscriptionDispatchReadyTasks(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers = null,
        int? reviewAutoRetryStopRound = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
    {
        return SubscriptionDispatchReadyBatch(
            kernel,
            workspace,
            goal,
            agents,
            profiles,
            providers,
            reviewAutoRetryStopRound,
            sandboxOptions,
            plannerSampleCount, conductorPolicy, cascadeTesterCheapFirst, cascadeCheapModelAlias, cascadeMechanicalReworkCheap).Dispatches;
    }

    public WorkerProfileReadyBatchResult SubscriptionDispatchReadyBatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers = null,
        int? reviewAutoRetryStopRound = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
    {
        ReconcileExitedAssignedProcessRecords(kernel, goal);
        goal = kernel.GetGoal(goal.Id);
        var safeBatch = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, agents);
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
            sandboxOptions: sandboxOptions,
            plannerSampleCount: ResolvePlannerSampleCount(workspace, plannerSampleCount),
            cascadeTesterCheapFirst: ResolveCascadeTesterCheapFirst(workspace, cascadeTesterCheapFirst, conductorPolicy),
            cascadeCheapModelAlias: ResolveCascadeCheapModelAlias(workspace, cascadeCheapModelAlias, conductorPolicy), cascadeMechanicalReworkCheap: ResolveCascadeMechanicalReworkCheap(workspace, cascadeMechanicalReworkCheap, conductorPolicy));
    }

    public WorkerProfileDispatchResult SubscriptionDispatchTask(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        DispatchModelOverride? modelOverride = null,
        bool allowGitReference = false,
        IModelProviderRegistry? providers = null,
        int? reviewAutoRetryStopRound = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
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
            reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound, conductorPolicy),
            citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
            plannerSampleCount: ResolvePlannerSampleCount(workspace, plannerSampleCount, conductorPolicy),
            cascadeTesterCheapFirst: ResolveCascadeTesterCheapFirst(workspace, cascadeTesterCheapFirst, conductorPolicy),
            cascadeCheapModelAlias: ResolveCascadeCheapModelAlias(workspace, cascadeCheapModelAlias, conductorPolicy), cascadeMechanicalReworkCheap: ResolveCascadeMechanicalReworkCheap(workspace, cascadeMechanicalReworkCheap, conductorPolicy));
    }

    private static CitedPriorEvidenceResolver CreateCitedPriorEvidenceResolver(OrchestratorWorkspace workspace) =>
        CitedPriorEvidenceResolver.ForWorkspace(workspace.SqliteStatePath, workspace.OrchestratorDirectory);

    private static int ResolveReviewAutoRetryStopRound(
        OrchestratorWorkspace workspace,
        int? reviewAutoRetryStopRound,
        ConductorAutonomyPolicy? conductorPolicy = null) =>
        reviewAutoRetryStopRound ??
        conductorPolicy?.ReviewAutoRetryStopRound ??
        ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(workspace.OrchestratorDirectory)).ReviewAutoRetryStopRound;

    private static int ResolvePlannerSampleCount(
        OrchestratorWorkspace workspace,
        int? plannerSampleCount,
        ConductorAutonomyPolicy? conductorPolicy = null) =>
        plannerSampleCount ??
        conductorPolicy?.PlannerSampleCount ??
        ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(
            new DirectoryInfo(workspace.OrchestratorDirectory)).PlannerSampleCount;

    private static bool ResolveCascadeTesterCheapFirst(OrchestratorWorkspace workspace, bool? explicitValue,
        ConductorAutonomyPolicy? policy = null) => explicitValue ?? policy?.CascadeTesterCheapFirst ??
        ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory)).CascadeTesterCheapFirst;

    private static bool ResolveCascadeMechanicalReworkCheap(OrchestratorWorkspace workspace, bool? explicitValue,
        ConductorAutonomyPolicy? policy = null) => explicitValue ?? policy?.CascadeMechanicalReworkCheap ??
        ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory)).CascadeMechanicalReworkCheap;

    private static string ResolveCascadeCheapModelAlias(OrchestratorWorkspace workspace, string? explicitValue,
        ConductorAutonomyPolicy? policy = null)
    {
        var alias = explicitValue ?? policy?.CascadeCheapModelAlias ??
            ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(new DirectoryInfo(workspace.OrchestratorDirectory)).CascadeCheapModelAlias;
        return !string.IsNullOrWhiteSpace(alias) ? alias : throw new ArgumentException("cascadeCheapModelAlias must not be empty.");
    }

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
            var assignedCandidates = goal.Tasks.Where(DispatchReadinessRules.IsSubscriptionStartCandidate).ToArray();
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
        if (GoalRefinementWorkCoordinator.HasPendingWork(current) &&
            TryHydrateStoreRefinedSpec(kernel, workspace, goal.Id))
        {
            current = kernel.GetGoal(goal.Id);
        }

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var outboxState = repository
            .GetOutboxStateAsync(GoalRefinementWorkCoordinator.MessageId(goal.Id))
            .GetAwaiter()
            .GetResult();
        var clarificationFacts = new GoalRefinementClarificationFacts(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .ListAsync(goal.Id.Value)
                .GetAwaiter()
                .GetResult());
        var readiness = GoalRefinementGate.Evaluate(current, outboxState, clarificationFacts);

        switch (readiness.Readiness)
        {
            case GoalRefinementReadiness.Ready:
                return;

            case GoalRefinementReadiness.AwaitingClarification:
                throw new InvalidOperationException(readiness.Detail);

            case GoalRefinementReadiness.Pending:
                ThrowPending(
                    workspace,
                    goal.Id,
                    readiness.Detail,
                    outboxState.Status,
                    outboxState.ProcessingStartedAt,
                    outboxState.Message.CreatedAt,
                    repaired: false);
                return;

            case GoalRefinementReadiness.RepairRequired:
                var ensured = repository
                    .EnsureOutboxMessageAsync(GoalRefinementWorkCoordinator.CreateMessage(goal.Id))
                    .GetAwaiter()
                    .GetResult();
                if (ensured.Disposition == OrchestratorStateOutboxEnsureDisposition.Failed)
                    ThrowFailed(goal.Id, ensured.State.Detail);
                if (ensured.Disposition == OrchestratorStateOutboxEnsureDisposition.Quarantined)
                    ThrowQuarantined(goal.Id, ensured.State.Detail);
                GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
                ThrowPending(
                    workspace,
                    goal.Id,
                    $"repair={ensured.Disposition.ToString().ToLowerInvariant()}",
                    ensured.State.Status,
                    ensured.State.ProcessingStartedAt,
                    ensured.State.Message.CreatedAt,
                    repaired: true);
                return;

            case GoalRefinementReadiness.Failed:
                if (SpecRefinementTransientFailureClassifier.IsTransient(readiness.Detail))
                {
                    if (StateDbWriteSession.IsActiveFor(workspace.SqliteStatePath))
                        throw new StateDbCommitBeforeRethrowException(
                            () => BuildTransientRetryException(workspace, goal.Id, outboxState!));
                    throw BuildTransientRetryException(workspace, goal.Id, outboxState!);
                }
                SpecRefinementTransientRetryStore.ForWorkspace(workspace).Reset(goal.Id);
                ThrowFailed(goal.Id, readiness.Detail);
                return;

            case GoalRefinementReadiness.Quarantined:
                ThrowQuarantined(goal.Id, readiness.Detail);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unknown goal-refinement readiness '{readiness.Readiness}'.");
        }

        static void ThrowPending(
            OrchestratorWorkspace workspace,
            GoalId goalId,
            string stateDetail,
            OrchestratorStateOutboxStatus outboxStatus,
            DateTimeOffset? processingStartedAt,
            DateTimeOffset createdAt,
            bool repaired)
        {
            if (StateDbWriteSession.IsActiveFor(workspace.SqliteStatePath))
            {
                throw new StateDbCommitBeforeRethrowException(
                    () => BuildPendingException(
                        workspace,
                        goalId,
                        stateDetail,
                        outboxStatus,
                        processingStartedAt,
                        createdAt,
                        repaired));
            }

            throw BuildPendingException(
                workspace,
                goalId,
                stateDetail,
                outboxStatus,
                processingStartedAt,
                createdAt,
                repaired);
        }

        static InvalidOperationException BuildPendingException(
            OrchestratorWorkspace workspace,
            GoalId goalId,
            string stateDetail,
            OrchestratorStateOutboxStatus outboxStatus,
            DateTimeOffset? processingStartedAt,
            DateTimeOffset createdAt,
            bool repaired)
        {
            var launch = GoalRefinementWorkCoordinator.TryLaunchIfDue(
                workspace,
                goalId,
                outboxStatus,
                processingStartedAt);
            var failedClaims = GoalRefinementWorkCoordinator.GetConsecutiveFailedClaims(workspace, goalId);
            var failedClaimsDetail = failedClaims > 0
                ? $" consecutive_failed_claims={failedClaims}"
                : string.Empty;
            return SpecRefinementPendingException(
                $"SPEC_REFINEMENT_PENDING goal={goalId.Value} owner=durable-outbox " +
                $"executor_started={launch.Started.ToString().ToLowerInvariant()} " +
                $"state={stateDetail} repaired={repaired.ToString().ToLowerInvariant()} " +
                $"pending_age={GoalRefinementWorkCoordinator.FormatPendingAge(createdAt)}" +
                $"{failedClaimsDetail} detail={launch.Detail}", outboxStatus);
        }

        static InvalidOperationException BuildTransientRetryException(
            OrchestratorWorkspace workspace, GoalId goalId, OrchestratorStateOutboxState failedState)
        {
            var retry = GoalRefinementWorkCoordinator.AdvanceTransientRetry(workspace, goalId, failedState);
            if (retry.ExhaustionDetail is { } exhaustion)
                return new InvalidOperationException(exhaustion);
            return SpecRefinementPendingException(
                $"SPEC_REFINEMENT_PENDING goal={goalId.Value} owner=durable-outbox state=transient-retry " +
                $"reason=retrying-after-transient-lock transient_retry_attempt={retry.Attempts} " +
                $"transient_retry_limit={GoalRefinementWorkCoordinator.ConsecutiveTransientRetryLimit} " +
                $"executor_started={retry.Launch.Started.ToString().ToLowerInvariant()} " +
                $"launch_detail={retry.Launch.Detail} last_failure={failedState.Detail}",
                OrchestratorStateOutboxStatus.Pending);
        }

        static void ThrowFailed(GoalId goalId, string? detail)
        {
            var failure = string.IsNullOrWhiteSpace(detail) ? "last-executor-failed" : detail;
            throw new InvalidOperationException(
                failure.StartsWith("SPEC_REFINEMENT_FAILED", StringComparison.Ordinal)
                    ? $"SPEC_REFINEMENT_FAILED goal={goalId.Value} {failure["SPEC_REFINEMENT_FAILED".Length..].TrimStart()}"
                    : $"SPEC_REFINEMENT_FAILED goal={goalId.Value} owner=durable-outbox phase=executor detail={failure}");
        }

        static void ThrowQuarantined(GoalId goalId, string? detail) =>
            throw new InvalidOperationException(
                $"SPEC_REFINEMENT_OPERATOR_RECOVERY goal={goalId.Value} owner=durable-outbox " +
                $"state=quarantined detail={detail ?? "operator-recovery-required"}");
    }

    private static bool TryHydrateStoreRefinedSpec(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        GoalId goalId)
    {
        var stored = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .LoadGoalAsync(goalId)
            .GetAwaiter()
            .GetResult();
        if (stored is null)
            return false;
        if (stored.RefinedSpec is null &&
            (stored.RefinedSpecVersions is null || stored.RefinedSpecVersions.Count == 0))
            return false;

        kernel.ReplaceGoalWithSnapshot(stored);
        return true;
    }

    private void ReconcileExitedAssignedProcessRecords(AgentOrchestratorKernel kernel, Goal goal)
    {
        var runner = new BackgroundDispatchRunner(
            isStillRunning: _isProcessRunning,
            readProcessIdentity: processId => _readProcessIdentity(processId) is { } identity
                ? (identity.StartedAt, identity.ImagePath)
                : null);
        StaleDispatchProcessReconciler.Reconcile(
            kernel,
            runner,
            goal,
            StaleDispatchProcessReconciler.AssignedOnly,
            _isProcessRunning,
            _readProcessIdentity);
    }

    private bool HasLiveTrackedProcess(TaskProcessRecord process)
        => StaleDispatchProcessReconciler.HasLiveOrUnknownTrackedProcess(
            process,
            _isProcessRunning,
            _readProcessIdentity);

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

    public ProcessBatchExecutionResult RefreshDispatches(
        AgentOrchestratorKernel kernel,
        Goal goal,
        BackgroundDispatchRunner? runner = null)
    {
        runner ??= new BackgroundDispatchRunner();
        var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.RefreshDispatches);
        var refreshed = new List<TaskSpec>();
        var outcomes = new List<DispatchRefreshOutcome>();

        foreach (var item in plan.Items.Where(item => item.Status == ProcessBatchItemStatus.Ready))
        {
            var task = goal.Tasks.Single(task => task.Id == item.TaskId);
            outcomes.Add(runner.RefreshLatestProcessWithOutcome(kernel, goal.Id, task.Id));
            refreshed.Add(task);
        }

        return new ProcessBatchExecutionResult(plan, refreshed, RefreshOutcomes: outcomes);
    }

    public ProcessBatchExecutionResult CancelDispatches(AgentOrchestratorKernel kernel, Goal goal)
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
