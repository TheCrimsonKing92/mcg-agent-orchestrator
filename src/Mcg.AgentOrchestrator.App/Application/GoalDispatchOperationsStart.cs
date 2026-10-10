using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Worker-process start and retry admission for prepared dispatches. Same owner as preparation, so
/// the injected process probes govern both readiness reconciliation and start recovery.
/// </summary>
internal sealed partial class GoalDispatchOperations
{
    public SubscriptionStartResult StartSubscriptionReadyTasks(
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
        WorkerSandboxOptions? sandboxOptions = null,
        int? plannerSampleCount = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        Action<GoalSnapshot>? recordDurableGoalBaseline = null,
        IReadOnlySet<TaskId>? excludedTaskIds = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null,
        Func<string, bool>? commandExists = null,
        Func<ClaudeCliAuthState>? claudeAuthProbe = null)
    {
        ReconcileExitedAssignedProcessRecords(kernel, goal);
        goal = kernel.GetGoal(goal.Id);
        var safeBatch = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, agents, approveHighRiskOwnership, excludedTaskIds);
        EnsureRefinedForSelectedTasks(kernel, workspace, providers, goal, safeBatch.TaskIds);
        goal = kernel.GetGoal(goal.Id);
        var retryReplayTasks = kernel.ExportGoalSnapshot(goal.Id).Tasks
            .Where(task => safeBatch.TaskIds.Contains(new TaskId(task.Id)) && task.LatestRetryAt is not null)
            .ToDictionary(task => new TaskId(task.Id));
        var home = _resolveHome(workspace);
        var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel,
            goal,
            agents,
            profiles,
            workspace.PromptDirectory,
            workspace.ResolveExecutionDirectory(goal.Id),
            DateTimeOffset.UtcNow,
            safeBatch.TaskIds,
            commandExists: commandExists,
            claudeAuthProbe: claudeAuthProbe,
            reviewAutoRetryStopRound: ResolveReviewAutoRetryStopRound(workspace, reviewAutoRetryStopRound, conductorPolicy),
            citedPriorEvidenceResolver: CreateCitedPriorEvidenceResolver(workspace),
            sandboxOptions: sandboxOptions,
            plannerSampleCount: ResolvePlannerSampleCount(workspace, plannerSampleCount, conductorPolicy),
            cascadeTesterCheapFirst: ResolveCascadeTesterCheapFirst(workspace, cascadeTesterCheapFirst, conductorPolicy),
            cascadeCheapModelAlias: ResolveCascadeCheapModelAlias(workspace, cascadeCheapModelAlias, conductorPolicy), cascadeMechanicalReworkCheap: ResolveCascadeMechanicalReworkCheap(workspace, cascadeMechanicalReworkCheap, conductorPolicy),
            orchestratorSkillDirectory: ResolveOrchestratorSkillDirectory(home), integrationBranch: workspace.IntegrationBranch,
            targetHome: new WorkerTargetHome(home.IsHome(workspace)));
        // Validate recovery authority before admission can hydrate a whole durable snapshot.
        // The runner repeats this guard immediately before launch to cover later state changes.
        var rejectedRecoveryIds = batch.Dispatches
            .Where(dispatch => BackgroundDispatchRunner.TryRejectInterruptedDispatchRecovery(
                kernel, goal.Id, dispatch.Task.Id, readCurrentInterruptedDispatchState))
            .Select(dispatch => dispatch.Task.Id)
            .ToHashSet();
        goal = kernel.GetGoal(goal.Id);
        var terminalRecoveryBlocks = goal.Status == GoalStatus.Active
            ? []
            : batch.Dispatches.Where(dispatch => !rejectedRecoveryIds.Contains(dispatch.Task.Id))
                .Select(dispatch => DispatchReadinessRules.BuildReadyBlockedDiagnostic(goal, dispatch.Task, agents,
                    "goal-not-active-after-recovery", [$"authoritative goal status is {goal.Status}"]))
                .ToArray();
        batch = batch with
        {
            Dispatches = batch.Dispatches.Where(dispatch =>
                !rejectedRecoveryIds.Contains(dispatch.Task.Id) && goal.Status == GoalStatus.Active).ToList(),
            Blocked = batch.Blocked.Concat(terminalRecoveryBlocks).ToArray()
        };
        var containsInterruptedDispatchRecovery = batch.Dispatches.Any(dispatch =>
            dispatch.Task.InterruptedDispatchRecoveryId is not null);

        var admittedTaskIds = new HashSet<TaskId>();
        foreach (var prepared in batch.Dispatches)
        {
            var currentTask = kernel.GetTask(goal.Id, prepared.Task.Id);
            if (currentTask.LastDispatch?.DispatchedAt != prepared.Task.LastDispatch?.DispatchedAt)
            {
                continue;
            }

            // Each paid reservation hydrates a whole goal. Preserve earlier admissions as well
            // as the mutations already accumulated before this batch.
            if (RequiresDurableAdmissionSnapshotReplacement(currentTask))
            {
                if (checkpointBeforeWorkerStart is null)
                    throw new InvalidOperationException(
                        $"Paid retry start requires a durable process checkpoint for task '{currentTask.Id}'.");
                checkpointBeforeWorkerStart(kernel, goal.Id, currentTask.Id, DispatchRecordCheckpointPhase.BeforeRetryAdmission);
                goal = kernel.GetGoal(goal.Id);
                currentTask = kernel.GetTask(goal.Id, prepared.Task.Id);
                if (currentTask.LastDispatch?.DispatchedAt != prepared.Task.LastDispatch?.DispatchedAt)
                    continue;
            }
            var admission = EnsurePreparedRetryAdmission(
                kernel,
                workspace,
                goal.Id,
                currentTask,
                retryReplayTask: retryReplayTasks.GetValueOrDefault(prepared.Task.Id),
                recordDurableGoalBaseline: recordDurableGoalBaseline);
            if (admission.AllowsProcessStart)
                admittedTaskIds.Add(prepared.Task.Id);
        }
        if (!containsInterruptedDispatchRecovery &&
            checkpointBeforeWorkerStart is not null &&
            admittedTaskIds.Count > 0)
        {
            checkpointBeforeWorkerStart(
                kernel,
                goal.Id,
                batch.Dispatches.First(dispatch => admittedTaskIds.Contains(dispatch.Task.Id)).Task.Id,
                DispatchRecordCheckpointPhase.BeforeProcessStart);
        }

        goal = kernel.GetGoal(goal.Id);
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
            sandboxOptions: sandboxOptions,
            admittedTaskIds: admittedTaskIds,
            recordDurableGoalBaseline: recordDurableGoalBaseline);
        return new SubscriptionStartResult(
            batch.Dispatches,
            processes with { RequeueSkippedCount = processes.RequeueSkippedCount + rejectedRecoveryIds.Count },
            safeBatch.Plan,
            OrderReadyBlockedDiagnostics(batch.Blocked, safeBatch.Blocked));
    }

    internal static IReadOnlyList<ReadyBlockedDiagnostic> OrderReadyBlockedDiagnostics(
        IReadOnlyList<ReadyBlockedDiagnostic> preflightBlocks,
        IReadOnlyList<ReadyBlockedDiagnostic> planExclusions) =>
        preflightBlocks.OrderBy(diagnostic => diagnostic.TaskNumber)
            .Concat(planExclusions)
            .ToList();

    public ProcessBatchExecutionResult StartDispatches(
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
        WorkerSandboxOptions? sandboxOptions = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        Action<GoalSnapshot>? recordDurableGoalBaseline = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
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
            sandboxOptions,
            conductorPolicy,
            recordDurableGoalBaseline: recordDurableGoalBaseline,
            cascadeTesterCheapFirst: cascadeTesterCheapFirst, cascadeCheapModelAlias: cascadeCheapModelAlias, cascadeMechanicalReworkCheap: cascadeMechanicalReworkCheap);
    }

    private ProcessBatchExecutionResult StartDispatches(
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
        WorkerSandboxOptions? sandboxOptions = null,
        ConductorAutonomyPolicy? conductorPolicy = null,
        IReadOnlySet<TaskId>? admittedTaskIds = null,
        Action<GoalSnapshot>? recordDurableGoalBaseline = null,
        bool? cascadeTesterCheapFirst = null,
        string? cascadeCheapModelAlias = null, bool? cascadeMechanicalReworkCheap = null)
    {
        runner ??= new BackgroundDispatchRunner();
        var logRoot = workspace.LogDirectory;
        var plan = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches);
        var started = new List<TaskSpec>();
        var recoveryActions = new List<WorkerSandboxPrepRecoverableAction>();
        var startFailures = new List<DispatchProcessStartFailure>();
        var startRefusals = new List<DispatchProcessStartRefusal>();
        var requeueSkippedCount = 0;
        IReadOnlyList<AgentDefinition>? resolvedAgents = null;
        WorkerProfileCatalog? resolvedProfiles = null;

        foreach (var item in plan.Items.Where(item =>
            item.Status == ProcessBatchItemStatus.Ready &&
            (taskIdsToStart is null || taskIdsToStart.Contains(item.TaskId))))
        {
            goal = kernel.GetGoal(goal.Id);
            if (goal.Status != GoalStatus.Active)
            {
                startRefusals.Add(new DispatchProcessStartRefusal(item.TaskId,
                    $"Goal is {goal.Status}; no further dispatch may start."));
                continue;
            }
            var task = goal.Tasks.Single(task => task.Id == item.TaskId);
            var recoveringPreparedReservation = HasRecoverablePreparedReservation(task);
            if (ShouldRefreshPreparedDispatchBeforeStart(task, refreshBeforeStart))
            {
                resolvedAgents ??= agents ?? AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
                resolvedProfiles ??= profiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath);
                try
                {
                    RefreshPreparedDispatchBeforeStart(
                        kernel,
                        workspace,
                        goal,
                        task,
                        resolvedAgents,
                        resolvedProfiles,
                        providers,
                        reviewAutoRetryStopRound,
                        sandboxOptions,
                        conductorPolicy: conductorPolicy,
                        cascadeTesterCheapFirst: cascadeTesterCheapFirst, cascadeCheapModelAlias: cascadeCheapModelAlias, cascadeMechanicalReworkCheap: cascadeMechanicalReworkCheap);
                }
                catch (DispatchAssignmentHoldException ex)
                {
                    kernel.RecordTaskNote(goal.Id, task.Id, ex.Hold.Message);
                    startRefusals.Add(new DispatchProcessStartRefusal(task.Id, ex.Hold.Message, ex.Hold));
                    continue;
                }
            }

            RetryAdmissionResult? admission = null;
            if (admittedTaskIds is null || !admittedTaskIds.Contains(task.Id))
            {
                if (BackgroundDispatchRunner.TryRejectInterruptedDispatchRecovery(
                    kernel, goal.Id, task.Id, readCurrentInterruptedDispatchState))
                {
                    requeueSkippedCount++;
                    goal = kernel.GetGoal(goal.Id);
                    continue;
                }
                if (RequiresDurableAdmissionSnapshotReplacement(task))
                {
                    if (checkpointBeforeWorkerStart is null)
                        throw new InvalidOperationException(
                            $"Paid retry start requires a durable process checkpoint for task '{task.Id}'.");
                    checkpointBeforeWorkerStart(kernel, goal.Id, task.Id, DispatchRecordCheckpointPhase.BeforeRetryAdmission);
                    goal = kernel.GetGoal(goal.Id);
                    task = goal.Tasks.Single(candidate => candidate.Id == item.TaskId);
                }
                admission = EnsurePreparedRetryAdmission(
                    kernel,
                    workspace,
                    goal.Id,
                    task,
                    recoveringPreparedReservation,
                    recordDurableGoalBaseline: recordDurableGoalBaseline);
                goal = kernel.GetGoal(goal.Id);
                task = goal.Tasks.Single(candidate => candidate.Id == item.TaskId);
                if (!admission.AllowsProcessStart)
                {
                    var reason = kernel.BuildProcessBatchPlan(goal.Id, ProcessBatchActionKind.StartDispatches).Items
                        .Single(candidate => candidate.TaskId == task.Id).Reason;
                    startRefusals.Add(new DispatchProcessStartRefusal(task.Id, reason));
                    continue;
                }
            }


            var startReceipt = admission?.Receipt ?? task.RetryAdmissionHistory.LastOrDefault(receipt =>
                receipt.LinkedDispatchAt == task.LastDispatch?.DispatchedAt &&
                receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
            var requiresDurableStartClaim = task.LatestRetryAt is not null &&
                task.LastDispatch?.PaidRoute == PaidRouteClassification.Paid;
            if (requiresDurableStartClaim &&
                (startReceipt is null || string.IsNullOrWhiteSpace(startReceipt.ReservationOwnerId)))
            {
                throw new InvalidOperationException(
                    $"Paid retry start requires a durable retry-admission receipt for task '{task.Id}'.");
            }
            if (requiresDurableStartClaim && checkpointBeforeWorkerStart is null)
            {
                throw new InvalidOperationException(
                    $"Paid retry start requires a durable process checkpoint for task '{task.Id}'.");
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
                            requestedPhase);
                    };
            var startResult = runner.TryStartLatestDispatch(
                kernel,
                goal.Id,
                task.Id,
                logRoot,
                batchCheckpoint,
                readCurrentInterruptedDispatchState,
                sandboxOptions,
                !requiresDurableStartClaim ||
                    startReceipt is null ||
                    string.IsNullOrWhiteSpace(startReceipt.ReservationOwnerId)
                    ? null
                    : () =>
                    {
                        var claim = RetryAdmissionReservationStore.TryClaimStartSnapshotAsync(
                            workspace.SqliteStatePath,
                            goal.Id,
                            task.Id,
                            startReceipt.LinkedDispatchAt,
                            startReceipt.ReservationOwnerId,
                            DateTimeOffset.UtcNow)
                            .GetAwaiter()
                            .GetResult();
                        if (claim is null || !claim.Claimed)
                            return false;
                        kernel.ReplaceGoalWithSnapshot(claim.Snapshot);
                        recordDurableGoalBaseline?.Invoke(claim.Snapshot);
                        return true;
                    },
                !requiresDurableStartClaim ||
                    startReceipt is null ||
                    string.IsNullOrWhiteSpace(startReceipt.ReservationOwnerId)
                    ? null
                    : () =>
                    {
                        var confirmation = RetryAdmissionReservationStore.TryConfirmStartAsync(
                                workspace.SqliteStatePath,
                                goal.Id,
                                task.Id,
                                startReceipt.LinkedDispatchAt,
                                startReceipt.ReservationOwnerId,
                                DateTimeOffset.UtcNow)
                            .GetAwaiter()
                            .GetResult();
                        if (confirmation is null || !confirmation.Claimed)
                            return false;
                        kernel.ReplaceGoalWithSnapshot(confirmation.Snapshot);
                        recordDurableGoalBaseline?.Invoke(confirmation.Snapshot);
                        return true;
                    });
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

            started.Add(kernel.GetTask(goal.Id, task.Id));
        }

        return new ProcessBatchExecutionResult(
            plan,
            started,
            recoveryActions,
            requeueSkippedCount,
            startFailures,
            StartRefusals: startRefusals);
    }

    private static RetryAdmissionResult EnsurePreparedRetryAdmission(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        GoalId goalId,
        TaskSpec task,
        bool reservationRecoveryConfirmed = false,
        TaskSnapshot? retryReplayTask = null,
        Action<GoalSnapshot>? recordDurableGoalBaseline = null)
    {
        var dispatch = task.LastDispatch ??
            throw new InvalidOperationException("Retry admission requires a prepared dispatch.");
        var fingerprint = dispatch.RetryContextFingerprint ??
            throw new InvalidOperationException("Prepared subscription dispatch is missing its retry-context fingerprint.");
        var recordedAt = DateTimeOffset.UtcNow;
        var reservationOwnerId = Guid.NewGuid().ToString("n");
        var reservationLeaseExpiresAt = recordedAt.AddMinutes(1);
        var retryMarkerAt = task.LatestRetryAt;
        if (task.LatestRetryAt is not null && dispatch.PaidRoute == PaidRouteClassification.Unknown)
        {
            throw new InvalidOperationException(
                $"Retry dispatch for task '{task.Id}' is missing an explicit paid-route classification.");
        }
        if (!RequiresDurableAdmissionSnapshotReplacement(task))
        {
            return kernel.RecordPreparedRetryAdmission(
                goalId,
                task.Id,
                fingerprint,
                dispatch.PaidRoute,
                recordedAt,
                RetryContextFingerprintFactory.GetOpenBlockingFindings(kernel.GetGoal(goalId)),
                reservationOwnerId,
                reservationLeaseExpiresAt,
                reservationRecoveryConfirmed);
        }

        var retryMessage = kernel.GetGoal(goalId).Timeline.LastOrDefault(evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.OccurredAt >= retryMarkerAt!.Value)?.Message ??
            throw new InvalidOperationException(
                $"Retry admission cannot persist task '{task.Id}' because its retry marker has no matching TaskRetried event.");

        var persisted = RetryAdmissionReservationStore.TryReserveAsync(
                workspace.SqliteStatePath,
                goalId,
                task.Id,
                fingerprint,
                dispatch.PaidRoute,
                task.PendingRetryCause,
                dispatch,
                recordedAt,
                reservationOwnerId,
                reservationLeaseExpiresAt,
                reservationRecoveryConfirmed,
                retryMarkerAt: retryMarkerAt,
                retryRoundKind: task.PendingRetryRoundKind,
                retryMessage: retryMessage,
                retryReplayTask: retryReplayTask)
            .GetAwaiter()
            .GetResult();
        if (persisted is not null)
        {
            var addedEvents = GoalEventsTimelineMirror.AddedEntries(kernel.GetGoal(goalId).Timeline, persisted.Snapshot.Timeline);
            kernel.ReplaceGoalStateWithSnapshot(persisted.Snapshot, persisted.HumanInputRequests ?? []);
            recordDurableGoalBaseline?.Invoke(persisted.Snapshot);
            GoalEventsTimelineMirror.AppendMissing(
                new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel, integrationBranch: workspace.IntegrationBranch),
                goalId, addedEvents, "retry-admission");
            return persisted.Admission;
        }

        throw new InvalidOperationException(
            $"Durable retry-admission reservation could not be created for goal '{goalId}' and task '{task.Id}'.");
    }

    private static bool RequiresDurableAdmissionSnapshotReplacement(TaskSpec task) =>
        task.LastDispatch is { PaidRoute: PaidRouteClassification.Paid } && task.LatestRetryAt is not null;

    private bool HasRecoverablePreparedReservation(TaskSpec task)
    {
        if (task.LastDispatch is not { } dispatch)
            return false;

        var receipt = task.RetryAdmissionHistory.LastOrDefault(candidate =>
            candidate.LinkedDispatchAt == dispatch.DispatchedAt &&
            candidate.WorkerStartedAt is null &&
            candidate.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation);
        if (receipt is null || receipt.WorkerStartClaimedAt is null)
            return receipt is not null;

        if (task.LastProcess is not { } process || HasLiveTrackedProcess(process))
            return task.LastProcess is null;

        const string outputSuffix = ".out.log";
        if (!process.StandardOutputPath.EndsWith(outputSuffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var startGatePath = process.StandardOutputPath[..^outputSuffix.Length] + ".start-gate";
        return !File.Exists(startGatePath);
    }

    /// <remarks>
    /// Instance rather than static because the recoverable-reservation check consults this
    /// instance's process-liveness probe.
    /// </remarks>
    internal bool ShouldRefreshPreparedDispatchBeforeStart(TaskSpec task, bool refreshBeforeStart) =>
        refreshBeforeStart &&
        (!HasRecoverablePreparedReservation(task) || !PreparedDispatchMatchesAcknowledgedAssignment(task));

    private static bool PreparedDispatchMatchesAcknowledgedAssignment(TaskSpec task) =>
        task.LastDispatch is { } preparedDispatch &&
        preparedDispatch.ConductorRoutingRevision == task.ConductorRoutingRevision &&
        (preparedDispatch.AssignedAgentId is not { Length: > 0 } preparedAgentId ||
         task.AssignedAgentId is { } assignedAgentId &&
         preparedAgentId.Equals(assignedAgentId.Value, StringComparison.OrdinalIgnoreCase));
}
